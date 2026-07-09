# Prompt for Claude — Continue the "SSMS Snippet Expander" Project (v3)

> Paste this whole file into a fresh Claude session (CLI or Cowork) to continue.
> Supersedes CLAUDE_PROMPT_V2.md. Current as of 2026-07-09, commit `16b34b0`.

---

You're working on SsmsSnippetExpander (github.com/Chaimemp/ssms-snippet-expander,
branch main). Read docs/DEVELOPMENT.md first — full history, architecture, every bug
solved, and a Session Log. Repo: `C:\Users\ChaimL.EMPEON\source\repos\SsmsSnippetExpander`
Shell is PowerShell. Use PowerShell syntax.

## TWO COMPONENTS

1. Tray app (repo root, net8.0-windows, `dotnet build -c Release`): global keyboard
   hook for SSMS. Tab expands SQL snippets (ssf, st100, ...), F12 scripts the object
   under the caret to a new query window, Ctrl+F12 locates it in Object Explorer via
   UI Automation. Working and in use.
2. VSIX extension (extension\, classic .NET Framework 4.7.2 VSIX targeting SSMS 22):
   same features in-process (real editor API, ServiceCache connection, SMO scripting,
   Object Explorer TreeView, MEF Tab command filter). Builds clean; INSTALLED into
   SSMS 22 and commands VERIFIED. Functional testing in progress.

## CURRENT STATE (2026-07-09, HEAD = 16b34b0 on main, pushed)

- Tray app: `dotnet build -c Release` => 0 warnings / 0 errors.
- Extension: builds clean via msbuild (exit 0), output
  `extension\bin\Release\SsmsSnippetExpander.Extension.vsix` (~26 KB / 25,963 bytes).
- Review pass #2 committed (961fdc3): fixed word-under-caret bug in
  `extension\GoToDefinitionService.cs` (F12 scripted the wrong object when the caret sat
  at the START of an identifier — now scans the line and picks the span containing the
  1-based caret column); added Tools > "Reload Snippets" command (Commands.vsct cmdid
  0x0102, wired in SnippetExpanderPackage.cs, `SnippetLibrary.Reload()` returns the
  count); added `.claude/` to `.gitignore`.
- **INSTALL RESOLVED.** The earlier double-click install NEVER deployed (Extensions
  folder had only cache files; DLL absent from disk; not in VS18 either). Fixed by
  installing SILENTLY with SSMS's OWN VSIXInstaller (exit 0):
  ```powershell
  Stop-Process -Name Ssms -Force
  & "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /quiet "<repo>\extension\bin\Release\SsmsSnippetExpander.Extension.vsix"
  ```
  DLL now on disk at `%LOCALAPPDATA%\Microsoft\SSMS\22.0_a29b2bf2\Extensions\`
  `pavzkn3g.oum\SsmsSnippetExpander.Extension.dll` (v0.1.0.0).
- **COMMANDS VERIFIED.** SSMS relaunched with `/log`; all three commands appear under the
  **Tools** menu ("Script Object Under Caret (F12)", "Locate Object in Object Explorer
  (Ctrl+F12)", "Reload Snippets (Snippet Expander)"). ActivityLog had 6 errors but ALL are
  pre-existing/unrelated (other package GUIDs; ours is `{7A1E4C9D-…}` and never faulted).
  NOTE: SSMS 22 has NO "Manage Extensions" UI — the Extensions menu only has "Customize
  Menu…"; our commands are parented to `IDM_VS_MENU_TOOLS` in Commands.vsct, so Tools is
  the place to look.
- Corrupt `.git\index` (garbage UU entries) was repaired earlier with
  `Remove-Item .git\index -Force; git reset` (working tree was fine — no --hard).
- Tray app confirmed NOT running (no Tab conflict); no Startup shortcut.
- Test server: VM-MSSQL01-DEV (SQL Server 2019, Windows auth EMPEON\ChaimL).

## WHERE WE ARE / NEXT STEP — functional testing, one step at a time in SSMS

Waiting on the user to run **Tools > Reload Snippets** and report the snippet COUNT
(0 would explain a dead ssf+Tab). Remaining tests, in a connected query window:

- type `ssf` then Tab  => `SELECT * FROM ⎸ WITH (NOLOCK)`
- type `st100` then Tab => `SELECT TOP 100 * FROM [TableName]` with TableName selected
- caret on a TABLE name, F12  => CREATE scripted to a new query window
- caret on a PROC name, F12   => CREATE scripted to a new query window
- Ctrl+F12 on an object name  => selected in Object Explorer

Rebuild/reinstall loop for fixes: `msbuild … /restore /p:Configuration=Release`
(add `/t:Rebuild` after .vsct changes) → close SSMS → `VSIXInstaller.exe /quiet <vsix>`
→ relaunch SSMS → have the user retest.

## RUNTIME FAILURES ARE EXPECTED TERRITORY

The undocumented SSMS-internal APIs can't be validated at compile time. For each symptom,
inspect the actual DLLs in
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`
(this is the csproj SsmsPath; SSMS build 22.1.2.0) and fix against reality. Watch-items:

- If `ssf`+Tab does NOTHING, the MEF Tab command filter never fired: check the
  [ContentType] attribute in TabExpansionFilter.cs — SSMS query windows may report a
  more specific content type than "text".
- F12/scripting depends on `ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo`
  and SMO `Script()`; Ctrl+F12 depends on IObjectExplorerService "Tree" reflection.

## MACHINE FACTS

- SSMS 22.1.2.0 at the SsmsPath above; extension\lib DLLs
  (SqlWorkbench.Interfaces.dll, SqlPackageBase.dll) already match 22.1.2.0.
- VS 18 Community with the VSSDK ("Visual Studio extension development") workload.
  Build the extension with msbuild, NOT dotnet build:

  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    extension\SsmsSnippetExpander.Extension.csproj /restore /p:Configuration=Release
  ```

  Add /t:Rebuild when the .vsct changed. MSB3277 unification warnings are known/benign.

## HARD-LEARNED RULES (do not violate)

- Never remove `<AllowUnsafeBlocks>` from the tray csproj (LibraryImport source-gen needs it).
- Keyboard-hook hot path must stay cheap (Windows drops slow LL hooks silently).
- Tray app and extension must NEVER run at the same time — both expand on Tab. Before
  testing the extension, ensure the tray exe isn't running
  (`Stop-Process -Name SsmsSnippetExpander -Force -ErrorAction SilentlyContinue`) and no
  Startup shortcut exists. (Currently: tray not running, no Startup shortcut.)
- If `.git\index.lock` exists it's stale — delete it.
- Log every milestone in docs/DEVELOPMENT.md's Session Log with the current date, and
  update docs/CLAUDE_PROMPT_V3.md's CURRENT STATE section before you finish.
