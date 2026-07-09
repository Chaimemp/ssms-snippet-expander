# Prompt for Claude — Continue the "SSMS Snippet Expander" Project (v3)

> Paste this whole file into a fresh Claude session (CLI or Cowork) to continue.
> Supersedes CLAUDE_PROMPT_V2.md. Current as of 2026-07-09, commit `84f1173`.

---

You're working on SsmsSnippetExpander (github.com/Chaimemp/ssms-snippet-expander,
branch main). Read docs/DEVELOPMENT.md first — full history, architecture, every
bug solved, and a Session Log. Primary dir:
`C:\Users\ChaimL.EMPEON\source\repos\SsmsSnippetExpander`

## TWO COMPONENTS

1. Tray app (repo root, net8.0-windows, `dotnet build -c Release`): global keyboard
   hook for SSMS. Tab expands SQL snippets (ssf, st100, ...), F12 scripts the object
   under the caret to a new query window, Ctrl+F12 locates it in Object Explorer via
   UI Automation. Working and in use.
2. VSIX extension (extension\, classic .NET Framework 4.7.2 VSIX targeting SSMS 22):
   same features in-process (real editor API, ServiceCache connection, SMO scripting,
   Object Explorer TreeView, MEF Tab command filter). Scaffolded from the open-source
   ssms-object-explorer-menu extension.

## CURRENT STATE (as of 2026-07-09, after review pass #2, pushed to main)

- Tray app: `dotnet build -c Release` => 0 warnings, 0 errors.
- Extension: builds clean via msbuild (exit 0). Output
  `extension\bin\Release\SsmsSnippetExpander.Extension.vsix` (~26 KB).
  NOT yet installed or tested in SSMS.
- Review pass #2 (on top of 84f1173): fixed the word-under-caret bug in
  `GoToDefinitionService.cs` (F12 scripted the wrong object when the caret sat at the
  start of an identifier — now scans the line and picks the span containing the caret
  column); added a **Tools → Reload Snippets** command (cmdid 0x0102 in `Commands.vsct`,
  wired in `SnippetExpanderPackage.cs`, `SnippetLibrary.Reload()` returns the count);
  added `.claude/` to `.gitignore`. A corrupt `.git\index` (garbage `UU` entries) was
  repaired with `Remove-Item .git\index -Force; git reset` — working-tree files were
  already correct, so no `--hard` was needed.

## MACHINE FACTS

- SSMS 22 build 22.1.2.0 installed at
  `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`
  (this is the csproj SsmsPath, and it's correct).
- Build the extension with msbuild, NOT dotnet build:

  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    extension\SsmsSnippetExpander.Extension.csproj /restore /p:Configuration=Release
  ```

  (VS 18 Community has the "Visual Studio extension development" / VSSDK workload.)
- Shell is PowerShell. Use PowerShell syntax.

## WHAT WAS FIXED TO GET THE EXTENSION TO COMPILE (all in commit 84f1173)

- Added framework reference System.Xml.Linq (CS1069, XDocument in SnippetLibrary.cs).
- Added framework reference System.Design (CS0012, MenuCommandService /
  OleMenuCommandService.AddCommand in SnippetExpanderPackage.cs).
- Added reference Microsoft.Data.SqlClient => `$(SsmsPath)\Microsoft.Data.SqlClient.dll`,
  Private=False (CS0012, SqlConnection in GoToDefinitionService.cs).
- CS1705 RegSvrEnum version clash: repo's extension\lib\SqlWorkbench.Interfaces.dll and
  SqlPackageBase.dll were SSMS build 22.3.24.0 (linked against RegSvrEnum v18.100), but
  this install ships RegSvrEnum v17.100 (build 22.1.2.0). Replaced both lib DLLs with
  the 22.1.2.0 copies from the SSMS IDE folder. Clash gone.
- Remaining msbuild output is only MSB3277 warnings (VS 17-vs-18 assembly unification),
  non-fatal.

## HARD-LEARNED RULES (do not violate)

- Never remove `<AllowUnsafeBlocks>` from the tray csproj — LibraryImport source-gen needs it.
- The keyboard-hook hot path must stay cheap (Windows silently drops slow LL hooks).
- Log each milestone in docs/DEVELOPMENT.md's Session Log with the current date.
- The tray app must NOT run at the same time as the extension — both expand on Tab.
  Exit the tray app (tray icon => Exit, or `Stop-Process -Name SsmsSnippetExpander`) and
  remove any Startup shortcut before installing/testing the extension.
- If `.git\index.lock` exists it's stale — delete it.

## NEXT STEP (not yet done)

Install and test the VSIX in SSMS 22:

1. Exit the tray app + remove its Startup shortcut.
2. Close all SSMS 22 windows.
3. Double-click `extension\bin\Release\SsmsSnippetExpander.Extension.vsix`, install to
   SQL Server Management Studio 22.
4. Launch SSMS, verify in Extensions > Manage Extensions > Installed.
5. In a query window (connected to a server/db), test: type `ssf`+Tab (expand),
   `st100`+Tab, F12 on an object name (script to new window), Ctrl+F12 (locate in
   Object Explorer).

Expect the interesting failures to be at RUNTIME in the undocumented SSMS-internal APIs
(ServiceCache connection, IObjectExplorerService tree walk, SMO scripting) — the compile
can't validate those paths. Report symptoms and fix against the actual SSMS DLLs.
