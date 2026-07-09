# SSMS Snippet Expander — Project Handoff / Claude Prompt

> Copy this whole file into Claude as context if you want to continue, rebuild, or hand this
> off to another machine/person. It captures the goal, everything that was built, every bug we
> hit, and the exact technical details.

---

## 1. Original Goal

The user wanted SQL Prompt–style snippet shortcuts in **SQL Server Management Studio (SSMS) 22**:
type a short code, press **Tab**, and have it expand to a full T-SQL statement. Examples:

| Shortcut | Expands to |
|----------|-----------|
| `ssf` | `SELECT * FROM ⎸ WITH (NOLOCK)` (caret between `FROM` and `WITH`) |
| `st100` | `SELECT TOP 100 * FROM [TableName]` (`TableName` selected) |

These shortcuts (SSF, ST100, IJ, LOJ, etc.) originate from **Redgate SQL Prompt**, a paid SSMS
add-in. The user does not have SQL Prompt and wanted the same behavior for free.

---

## 2. Key Discovery: Why SSMS Snippets Alone Don't Work

We first created native SSMS `.snippet` XML files and registered them via
**Tools → Code Snippets Manager**. They appeared, but typing `ssf` + Tab did **nothing**.

**Root cause:** SSMS's SQL editor does **not** support type-shortcut-then-Tab expansion.
The `<Shortcut>` element in snippet XML is honored by Visual Studio, but **ignored by the SSMS
SQL editor**. In SSMS, snippets can only be inserted via `Ctrl+K, Ctrl+X` (menu picker).
SQL Prompt achieves tab-expansion through its own add-in, not the native snippet system.

**Conclusion:** To replicate SQL Prompt's UX for free, we built a small standalone Windows app
that installs a global keyboard hook and performs the expansion itself.

---

## 3. What Was Built

### 3.1 The `.snippet` files (32 of them)

The canonical copies live **in the repo** under `snippets/*.snippet` and are **embedded into the
exe** (`<EmbeddedResource>` in the `.csproj`) so a standalone download works before anything is
copied anywhere. At runtime the app also reads on-disk copies from SSMS's snippet folder (which
override/extend the built-ins) — `Install.ps1` puts them there:
```
C:\Users\<user>\Documents\SQL Server Management Studio 22\Snippets\My Shortcuts\*.snippet
```

Each is standard Microsoft Code Snippet XML (`Format="1.0.0"`). Two shapes are used:

- **Literal + `$end$`** — e.g. `st100.snippet` (a `<Literal>` default that gets selected):

```xml
<?xml version="1.0" encoding="utf-8"?>
<CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
  <CodeSnippet Format="1.0.0">
    <Header>
      <Title>SELECT TOP 100</Title>
      <Shortcut>st100</Shortcut>
      <Description>SELECT TOP 100 * FROM [table]</Description>
      <Author>Custom</Author>
      <SnippetTypes>
        <SnippetType>Expansion</SnippetType>
      </SnippetTypes>
    </Header>
    <Snippet>
      <Declarations>
        <Literal>
          <ID>TableName</ID>
          <ToolTip>Table name</ToolTip>
          <Default>TableName</Default>
        </Literal>
      </Declarations>
      <Code Language="SQL"><![CDATA[SELECT TOP 100 * FROM [$TableName$]$end$]]></Code>
    </Snippet>
  </CodeSnippet>
</CodeSnippets>
```

- **`$end$` only** — e.g. `ssf.snippet` has empty `<Declarations>` and
  `<![CDATA[SELECT * FROM $end$ WITH (NOLOCK)]]>`, so the caret simply lands where `$end$` was.

**Full shortcut list (32):**

| Shortcut | Expansion |
|---|---|
| `ssf` | `SELECT * FROM ⎸ WITH (NOLOCK)` |
| `st10` / `st100` / `st1000` | `SELECT TOP N * FROM [TableName]` |
| `sc` | `SELECT COUNT(*) FROM` |
| `sd` | `SELECT DISTINCT col FROM` |
| `ij` | `INNER JOIN … ON` |
| `lj` | `LEFT JOIN … ON` |
| `loj` | `LEFT OUTER JOIN … ON` |
| `rj` | `RIGHT JOIN … ON` |
| `roj` | `RIGHT OUTER JOIN … ON` |
| `cj` | `CROSS JOIN` |
| `foj` | `FULL OUTER JOIN … ON` |
| `gb` | `GROUP BY` |
| `ob` | `ORDER BY` |
| `hav` | `HAVING` |
| `ii` | `INSERT INTO … VALUES` |
| `df` | `DELETE FROM … WHERE` |
| `uf` | `UPDATE … SET … WHERE` |
| `cp` | `CREATE OR ALTER PROCEDURE` |
| `ap` | `ALTER PROCEDURE` |
| `cv` | `CREATE OR ALTER VIEW` |
| `cf` | `CREATE OR ALTER FUNCTION` |
| `af` | `ALTER FUNCTION` |
| `cte` | `WITH [CTE] AS (…) SELECT` |
| `tr` | `BEGIN TRY … END CATCH` |
| `wl` | `WHILE … BEGIN END` |
| `ifex` | `IF EXISTS (SELECT 1 …)` |
| `ifnex` | `IF NOT EXISTS (SELECT 1 …)` |
| `cdb` | `USE [database]` |
| `isnl` | `IS NOT NULL` |
| `isn` | `IS NULL` |

Placeholders use the SSMS `$LiteralID$` convention. `$end$` marks the final caret position and
`$selected$` (unused here) marks surrounded text. The expander app strips `$end$`/`$selected$`
and substitutes each `$LiteralID$` with its `<Default>` value.

### 3.2 The expander app

- **Project:** `C:\Users\<user>\source\repos\SsmsSnippetExpander\`
- **Type:** .NET 8 WinForms tray app (`net8.0-windows`, `WinExe`, `AllowUnsafeBlocks=true`
  because it uses source-generated `LibraryImport` P/Invoke).
- **Single file:** `Program.cs` — an explicit `static class Program` with a `[STAThread] Main`
  (STA is required for the OLE clipboard; top-level statements can't mark the entry point
  `[STAThread]`), a `partial class MainForm`, and a `sealed record Snippet`.
- **Output:** `bin\Release\net8.0-windows\SsmsSnippetExpander.exe`

**How it works:**
1. Installs a global low-level keyboard hook (`WH_KEYBOARD_LL`) and mouse hook (`WH_MOUSE_LL`).
2. On each keystroke, if the **foreground process is SSMS**, it accumulates typed letters/digits
   into an in-memory buffer (the "current word").
3. When **Tab** is pressed and the buffer matches a known shortcut, it:
   - Suppresses the Tab (`return 1` from the hook),
   - Saves the clipboard, puts the expansion text on the clipboard,
   - Sends `deleteCount` backspaces **and** `Ctrl+V` as **one atomic `SendInput` batch** (paste
     handles newlines/brackets/Unicode reliably; batching stops a real keystroke landing mid-sequence),
   - After ~70 ms, sends Left / Shift+Left as a second atomic batch to move the caret to `$end$`
     (or select the first literal's default),
   - Restores the previous clipboard after ~600 ms.
4. Runs invisibly with a **system tray icon** (right-click: Show shortcuts / Reload / Exit).

---

## 4. Bugs Encountered & Fixed (chronological)

1. **`.csproj` referenced a missing `app.ico`** → build error `CS7064`. Removed the
   `<ApplicationIcon>` line; app uses `SystemIcons.Application`.

2. **`LibraryImport` needed unsafe blocks** → `CS0227`. Added
   `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.

3. **Multiple instances running simultaneously** (observed 3 live) → each installed its own hook
   → snippets would paste 3×. **Fix:** `Global\` named `Mutex` single-instance guard.

4. **`Process.GetProcessById` on every keystroke inside the LL hook.** Windows silently removes
   hooks slower than `LowLevelHooksTimeout` (~300 ms) and it lags system-wide typing. **Fix:**
   cache the foreground-window → isSSMS result; only re-resolve the process when the foreground
   `HWND` changes (`GetForegroundWindow` is cheap).

5. **`Invoke()` could throw** because the form's window handle is never created (form is never
   shown). **Fix:** force handle creation in ctor via `_ = Handle;`.

6. **`Clipboard.SetText` can throw** if the clipboard is transiently locked → shortcut left
   half-deleted. **Fix:** `TrySetClipboardText` retries 5× with 20 ms backoff; aborts cleanly.

7. **`GetKeyState` unreliable inside a global hook** (reads queued, not physical, state).
   **Fix:** switched to `GetAsyncKeyState`.

8. **"Nothing happens" — cause #1: doubled `$$`.** The PowerShell that generated the first 4
   snippet files (`ssf`, `st10`, `st100`, `st1000`) mis-escaped `$` and produced
   `[$$TableName$$]$$end$$` instead of `[$TableName$]$end$`. **Fix:** replaced `$$`→`$` across
   all files. (The other 28 files, generated via a here-string helper, were already correct.)

9. **"Nothing happens" — cause #2 (the real one): OneDrive Documents redirection.**
   `Environment.SpecialFolder.MyDocuments` resolved to
   `C:\Users\<user>\OneDrive - empeon.com\Documents`, but SSMS and the snippet files live in the
   **physical** `C:\Users\<user>\Documents`. The loader found an SSMS folder in OneDrive with no
   `My Shortcuts` subfolder → **`snippets=0`** (confirmed via diagnostic log). **Fix:** scan BOTH
   `%USERPROFILE%\Documents` and the redirected `MyDocuments` path, de-duplicated.

10. **"Nothing happens" — cause #3: stale buffer.** Diagnostic log showed
    `TAB in SSMS. typed='nssf', match=False` — a stray `n` had glued onto `ssf` from earlier
    typing, so the word wasn't a valid shortcut (this proved the hook/focus/matching all work).
    **Fix (robustness):** (a) idle reset — clear the buffer if >2 s elapsed since the last
    keystroke; (b) mouse hook — clear the buffer on any mouse click (caret moved).

---

## 5. Diagnostics

The app writes a log to `%TEMP%\SsmsSnippetExpander.log`, recording startup (hook handle +
snippet count + keys), every Tab press in SSMS (the captured word + whether it matched), and each
expansion attempt. This is how bugs #9 and #10 were pinpointed. Logging is **off by default**;
run the exe with `--debug` to enable it.

---

## 6. Environment Facts (this machine)

- OS: Windows 11 Pro. Shell: PowerShell 7+.
- SSMS: **v22** installed at `C:\Program Files\Microsoft SQL Server Management Studio 22\`
  (also v21 present). SSMS 22 is the 64-bit, Visual-Studio-shell-based release.
- SSMS **process name: `SSMS`** (matched via `StartsWith("Ssms", OrdinalIgnoreCase)`).
- Both SSMS and the expander run **non-elevated** (ruled out UIPI/elevation blocking).
- Native snippet folder registered in SSMS: the physical
  `C:\Users\<user>\Documents\SQL Server Management Studio 22\Snippets\My Shortcuts`.
- .NET SDK 10 present; project targets `net8.0-windows`.

---

## 7. Build / Run / Autostart

```powershell
# Build
cd "$env:USERPROFILE\source\repos\SsmsSnippetExpander"
dotnet build -c Release

# Run (tray app — no window; look near the clock / under the ^ overflow)
Start-Process "bin\Release\net8.0-windows\SsmsSnippetExpander.exe"

# Stop
Stop-Process -Name "SsmsSnippetExpander" -Force

# Read diagnostics
Get-Content "$env:TEMP\SsmsSnippetExpander.log"
```

**Run at login** (Startup shortcut):
```powershell
$exe = "$env:USERPROFILE\source\repos\SsmsSnippetExpander\bin\Release\net8.0-windows\SsmsSnippetExpander.exe"
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\SsmsSnippetExpander.lnk")
$lnk.TargetPath = $exe
$lnk.Save()
```

---

## 8. Usage

1. Launch the exe (tray icon appears; a startup balloon confirms N shortcuts active).
2. In an SSMS query window, type a shortcut (e.g. `ssf`) and press **Tab** → it expands.
3. Right-click the tray icon → **Show shortcuts…**, **Reload snippets** (after adding new
   `.snippet` files), or **Exit**.

**Inherent caveat (same as SQL Prompt):** if a real identifier equals a shortcut (e.g. a column
literally named `df`), then `df` + Tab expands it. Undo with `Ctrl+Z`, or don't press Tab there.

---

## 9. Known Limitations / Possible Future Work

- **No tab-through of multiple literals.** The caret jumps to (and selects) the *first*
  literal's default, or lands at `$end$` — implemented via Left / Shift+Left after paste —
  but it cannot Tab from one field to the next. That would require a real SSMS add-in.
- **Buffer is a heuristic**, not a true editor-token reader. Idle-reset + mouse-reset cover the
  common desync cases, but rapid consecutive typing across a boundary can still mis-capture.
- **Clipboard restore is text-only** — an image or file list on the clipboard is lost when an
  expansion fires (only text is saved/restored).
- **Letter mapping assumes a QWERTY-style layout** (VK code ≈ ASCII); non-Latin layouts may
  mis-capture the buffer.
- **No installer / not signed** — runs from the build output folder.

Resolved since first writing: diagnostic logging is now **opt-in** (`--debug` flag), and
`keybd_event` was replaced with a single batched **`SendInput`** call (atomic — user keystrokes
can't interleave mid-expansion).

---

## 10. Full `Program.cs`

The authoritative source is `C:\Users\<user>\source\repos\SsmsSnippetExpander\Program.cs`.
It contains: single-instance mutex, `MainForm` with keyboard + mouse LL hooks, cached SSMS
focus detection, clipboard-based paste expansion with retry + restore, idle/mouse buffer resets,
a multi-path (`%USERPROFILE%\Documents` + OneDrive `MyDocuments`) snippet loader that parses the
`.snippet` XML and substitutes `$Literal$` defaults, tray UI, and `%TEMP%` diagnostic logging.

---

## 11. Session Log

### 2026-07-08 — Restored missing `AllowUnsafeBlocks`

- **Task:** Ran `dotnet build SsmsSnippetExpander.csproj -c Release`.
- **Symptom:** Build failed with `SYSLIB1062` and multiple `CS0227` (`Unsafe code may only
  appear if compiling with /unsafe`), originating from the source-generated `LibraryImports.g.cs`
  produced by the `[LibraryImport]` P/Invoke declarations.
- **Root cause:** The `.csproj` was missing `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — the
  same issue documented as bug #2 above. The working tree had drifted from this doc; the setting
  was no longer present in the project file.
- **Fix:** Re-added `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to the `<PropertyGroup>` in
  `SsmsSnippetExpander.csproj` (right after `<ImplicitUsings>`).
- **Result:** `dotnet build -c Release` succeeded (0 warnings, 0 errors). Output at
  `bin\Release\net8.0-windows\SsmsSnippetExpander.dll`.

### 2026-07-08 — Code review: hook-logic fixes + SendInput

Code review pass over `Program.cs`; all fixes verified by a successful Release build.

- **Alt+Tab triggered expansion** (worst bug): after typing a shortcut, Alt+Tab arrives as a
  `WM_SYSKEYDOWN` Tab and matched the expansion branch — suppressing the app switch and
  expanding. Ctrl/Alt chords (Ctrl+S, Ctrl+Z, Alt+Tab, …) now clear the buffer and pass through
  (`LLKHF_ALTDOWN` flag + `GetAsyncKeyState(VK_CONTROL)`).
- **Ctrl chords polluted the buffer:** Ctrl+S etc. appended the letter, corrupting the
  backspace count for the next expansion. Covered by the same chord check.
- **Shift keydown cleared the buffer** (typing any uppercase letter broke matching); modifier
  keydowns (Shift/Ctrl/Alt L+R, CapsLock, Win) are now ignored entirely.
- **Shift+top-row digit** types a symbol but buffered the digit — now resets the buffer.
- **Shift+Tab** (unindent) no longer expands.
- **Numpad digits** (VK 0x60–0x69) now feed the buffer, so `st100` works from the numpad.
- **Stale "N snippets loaded" menu label** after Reload — now updated alongside tray text.
- **`keybd_event` → batched `SendInput`:** backspaces + Ctrl+V (and the later caret-positioning
  Left/Shift+Left sequence) are each sent as one atomic batch, so user keystrokes can't land in
  the middle. Structs: `INPUT`/`KEYBDINPUT` + explicit-layout union incl. `MOUSEINPUT` for size.
- **`$$` escape** in snippet `<Code>` now emits a literal `$` (regex `\$(\w*)\$`, empty id = `$`).
- **Regression during the review (fixed):** `AllowUnsafeBlocks` was removed from the csproj as
  "unused" — wrong; `[LibraryImport]` source-gen requires it (bug #2). Re-added; build green.

### 2026-07-08 — F12 / Ctrl+F12 (tray app) + VSIX extension scaffold

**Tray app v1.1.0** — new files `Navigation.cs`, `SqlObjectService.cs`, `ObjectExplorerNavigator.cs`:

- **F12 in SSMS** scripts the table/view/proc/function under the caret into a new query window.
  Flow: parse server+db from the SSMS window title → capture the word under the caret
  (Ctrl+Right, Ctrl+Shift+Left, Ctrl+C — clipboard saved/restored) → resolve via
  `sys.objects` (Microsoft.Data.SqlClient, Windows auth, `TrustServerCertificate`) →
  modules scripted from `sys.sql_modules`, tables rebuilt from catalog views (columns,
  identity, computed, defaults, collation, PK/UQ/FK, indexes) → clipboard + Ctrl+N + Ctrl+V
  (new-window readiness detected by window-title change).
- **Ctrl+F12** locates the object in Object Explorer via UI Automation (F8 first, then walk
  Server → Databases → db → type folder; lazy nodes handled with polling). Best-effort.
- Per-server connection overrides: `%APPDATA%\SsmsSnippetExpander\connections.json`
  (`{"SERVER": "connection string"}`) for SQL logins.
- csproj: `Microsoft.Data.SqlClient 5.2.2`, `FrameworkReference Microsoft.WindowsDesktop.App`
  (for `System.Windows.Automation`), `extension\**` excluded from the SDK build.
- Hook: F12/Ctrl+F12 intercepted (before the chord filter), `_navBusy` reentrancy guard,
  `Balloon` made thread-safe.

**VSIX extension scaffold** — `extension\` folder, targeting SSMS 22 (`Microsoft.VisualStudio.Ssms
[22.0,)`, amd64), modeled on ssms-object-explorer-menu (tested on SSMS 22.x):

- Same F12/Ctrl+F12 features in-process: DTE EditPoint for the caret word,
  `ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo` for the real connection,
  SMO `Script()` for CREATE, `CreateNewBlankScript` for the new window, and the actual
  Object Explorer TreeView (reflection on `IObjectExplorerService.Tree`).
- Requires Visual Studio + "Visual Studio extension development" workload to build —
  see `extension\README.md`. NOT built by `dotnet build`; expect first-build friction
  (undocumented SSMS-internal APIs).
- **Tab-snippet expansion is also in-process** (`SnippetLibrary.cs` +
  `TabExpansionFilter.cs`): a MEF `IVsTextViewCreationListener` adds an
  `IOleCommandTarget` filter per editable view, intercepts `VSStd2K TAB`, replaces the
  word before the caret via a real `ITextEdit`, and places the caret/selection
  directly. Manifest gained a MefComponent asset. Snippets load from the same
  Documents folders (no embedded built-ins in the extension).
- Don't run the tray app and the extension simultaneously — both expand on Tab.
- `Install.ps1` now stops a running tray instance before building (exe lock).
- Once the extension proves out on SSMS 22, the tray app retires.
