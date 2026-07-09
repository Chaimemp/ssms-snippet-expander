# SSMS Snippet Expander — VSIX Extension (experimental)

The in-process version of the tray app, built the way Redgate SQL Prompt and Devart
SQL Complete do it: a VSPackage that loads inside SSMS 22.

| Shortcut | Action |
|---|---|
| *shortcut* + **Tab** | Expand a snippet shortcut (`ssf`, `st100`, …) — real buffer edit via an editor command filter, with caret/first-field placement |
| **F12** | Script the table / view / procedure / function under the caret to a new query window (via SMO, on the window's own connection) |
| **Ctrl+F12** | Select the object in Object Explorer |

Snippets are read from `Documents\SQL Server Management Studio*\Snippets\My Shortcuts`
(run the repo's `Install.ps1` to copy them there; unlike the tray app there are no
embedded built-ins).

> **Don't run the tray app and the extension at the same time** — both would expand
> on Tab, doubling the insert. Exit the tray app once the extension is installed.

Modeled on [ssms-object-explorer-menu](https://github.com/brink-daniel/ssms-object-explorer-menu)
(open source, actively tested against SSMS 22.x). More extensions:
[SSMS Extension Gallery](https://erikej.github.io/SsmsExtensions/).

## Why this beats the tray app

Runs inside SSMS, so it uses the editor's real caret/text API (no clipboard tricks),
the query window's actual connection object (no window-title parsing), SSMS's own SMO
scripting engine, and the real Object Explorer TreeView (no UI Automation).

## Build requirements

1. **Visual Studio 2022 or later** (free Community edition works) with the
   **"Visual Studio extension development"** workload — the plain .NET SDK cannot
   build VSIX projects.
2. SSMS 22 installed. If not at
   `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`,
   fix the `SsmsPath` property in the `.csproj` (it's used to reference SMO DLLs).

## Build

Open `SsmsSnippetExpander.Extension.csproj` in Visual Studio and build (Release), or:

```powershell
msbuild SsmsSnippetExpander.Extension.csproj /restore /p:Configuration=Release
```

Output: `bin\Release\SsmsSnippetExpander.Extension.vsix`

## Install into SSMS 22

Double-click the `.vsix` (the installer detects SSMS 22 as a target), or copy the
extension folder manually per
[Microsoft's instructions](https://learn.microsoft.com/ssms/install-extensions-in-sql-server-management-studio-ssms).
Restart SSMS. The commands also appear under **Tools** so you can verify loading
(and rebind keys under Tools → Options → Keyboard if F12 conflicts).

## Known limitations / first-build notes

- **Expect first-build friction.** The SSMS-internal APIs (`ServiceCache`,
  `IObjectExplorerService`) are undocumented; if SSMS 22 renamed something, the
  compiler or a runtime error will say so — report the exact message back to fix it.
- `lib\SqlWorkbench.Interfaces.dll` / `lib\SqlPackageBase.dll` are SSMS-internal
  assemblies (copied from the ssms-object-explorer-menu repo). If they're outdated
  for your SSMS build, replace them with the same-named files from the SSMS install
  folder.
- The Tab filter is registered for all editable text views ("text" content type),
  which in SSMS means query windows; if it ever fires somewhere unwanted, the
  content-type attribute in `TabExpansionFilter.cs` is the knob to tighten.
- Snippets load once per SSMS session (no Reload command yet — restart SSMS after
  editing .snippet files).
- Triggers and cross-database objects aren't handled.
