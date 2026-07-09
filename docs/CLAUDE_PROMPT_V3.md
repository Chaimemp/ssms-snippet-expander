# Prompt for Claude — Continue the "SSMS Snippet Expander" Project (v3)

> Paste this whole file into a fresh Claude session (CLI or Cowork) to continue.
> Supersedes CLAUDE_PROMPT_V2.md. Current as of 2026-07-09, commit `961fdc3`.

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
   Object Explorer TreeView, MEF Tab command filter). Builds clean; installed into
   SSMS 22 but NOT yet verified/tested there.

## CURRENT STATE (2026-07-09, HEAD = 961fdc3 on main, pushed)

- Tray app: `dotnet build -c Release` => 0 warnings / 0 errors.
- Extension: builds clean via msbuild (exit 0), output
  `extension\bin\Release\SsmsSnippetExpander.Extension.vsix` (~26 KB / 25,963 bytes).
- Review pass #2 is committed (961fdc3): fixed word-under-caret bug in
  `extension\GoToDefinitionService.cs` (F12 scripted the wrong object when the caret sat
  at the START of an identifier — now scans the line and picks the span containing the
  1-based caret column); added Tools > "Reload Snippets" command (Commands.vsct cmdid
  0x0102, wired in SnippetExpanderPackage.cs, `SnippetLibrary.Reload()` returns the
  count); added `.claude/` to `.gitignore`.
- A corrupt `.git\index` (garbage UU entries) was repaired earlier with
  `Remove-Item .git\index -Force; git reset` (working tree was already correct — no --hard).
- The .vsix was just double-click-installed by the user. It does NOT appear on disk yet
  (searched %LOCALAPPDATA%\Microsoft and the SSMS install tree) because the VSIX installer
  STAGES the extension and SSMS finalizes it on next launch. SSMS had to be closed for the
  install (done: `Stop-Process -Name Ssms -Force`). User is about to launch SSMS.

## WHERE WE ARE / NEXT STEP

The user is launching SSMS 22 to finalize + verify the extension. Guide them:

1. Extensions > Manage Extensions > Installed — confirm "SSMS Snippet Expander Extension"
   is present + enabled.
2. Tools menu — confirm the commands appear, incl. "Reload Snippets".
3. In a query window connected to a server+db, test:
   - `ssf` + Tab  => SELECT * FROM expansion
   - `st100` + Tab => SELECT TOP 100 * FROM [TableName] with TableName selected
   - F12 on a TABLE name => CREATE scripted to a new query window
   - F12 on a PROC name  => CREATE scripted to a new query window
   - Ctrl+F12 on an object name => selected in Object Explorer
   - Tools > Reload Snippets => shows a count

After confirming the DLL landed, re-scan to locate the installed copy.
If the extension is NOT in the Installed list, read the SSMS ActivityLog
(%APPDATA%\Microsoft\SQL Server Management Studio\22.0\ActivityLog.xml or the SSMS
AppData folder) to see why it was skipped.

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
