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
| `ssf` | `SELECT * FROM [TableName]` |
| `st100` | `SELECT TOP 100 * FROM [TableName]` |

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

Location (physical, non-OneDrive path):
```
C:\Users\<user>\Documents\SQL Server Management Studio 22\Snippets\My Shortcuts\*.snippet
```

Each is standard Microsoft Code Snippet XML (`Format="1.0.0"`), e.g. `ssf.snippet`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
  <CodeSnippet Format="1.0.0">
    <Header>
      <Title>SELECT * FROM</Title>
      <Shortcut>ssf</Shortcut>
      <Description>SELECT * FROM [table]</Description>
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
      <Code Language="SQL"><![CDATA[SELECT * FROM [$TableName$]$end$]]></Code>
    </Snippet>
  </CodeSnippet>
</CodeSnippets>
```

**Full shortcut list (32):**

| Shortcut | Expansion |
|---|---|
| `ssf` | `SELECT * FROM [table]` |
| `st10` / `st100` / `st1000` | `SELECT TOP N * FROM [table]` |
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
- **Single file:** `Program.cs` (top-level statements + `partial class MainForm`).
- **Output:** `bin\Release\net8.0-windows\SsmsSnippetExpander.exe`

**How it works:**
1. Installs a global low-level keyboard hook (`WH_KEYBOARD_LL`) and mouse hook (`WH_MOUSE_LL`).
2. On each keystroke, if the **foreground process is SSMS**, it accumulates typed letters/digits
   into an in-memory buffer (the "current word").
3. When **Tab** is pressed and the buffer matches a known shortcut, it:
   - Suppresses the Tab (`return 1` from the hook),
   - Saves the clipboard, puts the expansion text on the clipboard,
   - Sends `deleteCount` backspaces to erase the typed shortcut,
   - Sends `Ctrl+V` to paste the expansion (handles newlines/brackets/Unicode reliably),
   - Restores the previous clipboard after 600 ms.
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
expansion attempt. This is how bugs #9 and #10 were pinpointed. **Consider removing or gating the
logging behind a flag for a "release" build.**

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

- **Expansion loses `$end$` caret positioning and tab-through of literals.** The app pastes the
  expanded text with default literal values inline; it does not reposition the caret to `$end$`
  or let you Tab between literals (native SSMS snippet insertion does, but only via the menu).
  A future version could position the caret or select the first literal.
- **Buffer is a heuristic**, not a true editor-token reader. Idle-reset + mouse-reset cover the
  common desync cases, but rapid consecutive typing across a boundary can still mis-capture.
- **Diagnostic logging is always on** — gate it behind a flag or remove for release.
- **No installer / not signed** — runs from the build output folder.
- **`keybd_event` is legacy** — `SendInput` is the modern equivalent (works fine as-is).

---

## 10. Full `Program.cs`

The authoritative source is `C:\Users\<user>\source\repos\SsmsSnippetExpander\Program.cs`.
It contains: single-instance mutex, `MainForm` with keyboard + mouse LL hooks, cached SSMS
focus detection, clipboard-based paste expansion with retry + restore, idle/mouse buffer resets,
a multi-path (`%USERPROFILE%\Documents` + OneDrive `MyDocuments`) snippet loader that parses the
`.snippet` XML and substitutes `$Literal$` defaults, tray UI, and `%TEMP%` diagnostic logging.
