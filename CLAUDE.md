# SsmsSnippetExpander — project memory for Claude Code

Repo: github.com/Chaimemp/ssms-snippet-expander (branch main). Shell: PowerShell.
For full history read docs/DEVELOPMENT.md (Session Log at the bottom).
For the latest state read the CURRENT STATE section of docs/CLAUDE_PROMPT_V3.md.

## Components

1. **Tray app** (repo root, net8.0-windows) — global keyboard hook for SSMS.
   Tab expands SQL snippets (ssf, st100, …), F12 scripts the object under the caret
   to a new window, Ctrl+F12 locates it in Object Explorer via UI Automation.
   Build: `dotnet build -c Release` (kill SsmsSnippetExpander.exe first — file lock).
2. **VSIX extension** (extension\, .NET Framework 4.7.2, targets SSMS 22) — same
   features in-process. Build with msbuild, NEVER dotnet build:
   `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" extension\SsmsSnippetExpander.Extension.csproj /restore /p:Configuration=Release`
   (add `/t:Rebuild` after .vsct changes). MSB3277 warnings are known/benign.

## Machine facts

- SSMS 22 build 22.1.2.0 at
  `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`
  (= csproj `SsmsPath`). extension\lib DLLs match 22.1.2.0.
- VS 18 Community with the VSSDK ("Visual Studio extension development") workload.

## Hard-learned rules (do not violate)

- NEVER remove `<AllowUnsafeBlocks>` from the tray csproj — LibraryImport source-gen
  needs it (breaks with CS0227/SYSLIB1062).
- Keyboard-hook hot path must stay cheap — Windows silently drops slow LL hooks.
- Tray app and extension must NEVER run simultaneously (both expand on Tab):
  `Stop-Process -Name SsmsSnippetExpander -Force -ErrorAction SilentlyContinue`
- If `.git\index.lock` exists it's stale — delete it. This repo once had a corrupt
  index (garbage UU entries); fix = `Remove-Item .git\index -Force; git reset`
  (working tree was fine — don't use --hard without checking).
- The SSMS-internal APIs (ServiceCache, IObjectExplorerService "Tree" reflection,
  SMO scripting, MEF Tab filter) are undocumented — compile success proves nothing;
  diagnose runtime failures against the real DLLs in the SsmsPath folder.
- Known watch-item: if ssf+Tab does nothing in the extension, check
  `[ContentType("text")]` in TabExpansionFilter.cs (SSMS may use a more specific
  content type) and the MefComponent asset in source.extension.vsixmanifest.

## Working agreement

- Work autonomously; only ask the user to act for GUI-only steps inside SSMS
  (clicking menus, typing test shortcuts), one instruction at a time.
- After every milestone: commit + push, add a dated entry to docs/DEVELOPMENT.md's
  Session Log, and refresh the CURRENT STATE section in docs/CLAUDE_PROMPT_V3.md.
- Testing checklist for the extension (in a connected query window): ssf+Tab,
  st100+Tab (TableName gets selected), F12 on a table name, F12 on a proc name,
  Ctrl+F12 (selects in Object Explorer), Tools > Reload Snippets (shows a count).
