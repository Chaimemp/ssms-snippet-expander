# SSMS Snippet Expander

A tiny, free Windows tray app that brings **SQL Prompt–style tab-expansion** to
**SQL Server Management Studio (SSMS)**. Type a short code, press **Tab**, and it
expands into a full T-SQL statement — with the cursor placed exactly where you need it.

```
ssf   + Tab  →  SELECT * FROM ⎸ WITH (NOLOCK)
st100 + Tab  →  SELECT TOP 100 * FROM [TableName]      ← "TableName" is selected
ij    + Tab  →  INNER JOIN [TableName] AS [t] ON ...   ← first field selected
tr    + Tab  →  BEGIN TRY ⎸ END TRY BEGIN CATCH ...    ← cursor inside the block
```

No add-in, no license, no admin rights required.

---

## Why this exists

SSMS's SQL editor **ignores the `<Shortcut>` element** in native `.snippet` files —
unlike Visual Studio, it has no built-in "type shortcut + Tab" expansion. Native
snippets can only be inserted through the `Ctrl+K, Ctrl+X` menu picker. Paid add-ins
like Redgate SQL Prompt add tab-expansion because they hook into SSMS directly.

This app reproduces that behavior with a lightweight global keyboard hook: when SSMS
is focused, it watches for a known shortcut followed by Tab and pastes the expansion.

---

## Requirements

- Windows 10/11
- SQL Server Management Studio (any recent version; tested on **SSMS 22**)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build) — or grab a
  prebuilt `SsmsSnippetExpander.exe` if one is attached to a release

---

## Install

```powershell
git clone https://github.com/<your-user>/SsmsSnippetExpander.git
cd SsmsSnippetExpander
.\Install.ps1            # copies the snippets and builds the app
```

Then launch the app:

```powershell
.\bin\Release\net8.0-windows\SsmsSnippetExpander.exe
```

A tray icon appears near the clock (it may be under the `^` overflow arrow) and a
balloon confirms how many shortcuts are active.

**Run it automatically at login:**

```powershell
.\Install.ps1 -Startup
```

**Other options:**

```powershell
.\Install.ps1 -NoBuild   # just copy the snippet files, don't build
```

---

## Usage

1. Make sure the app is running (tray icon present).
2. In an SSMS query window, type a shortcut and press **Tab**.
3. The shortcut expands and the cursor lands on the first field (selected) or at the
   marked spot — start typing to replace it.

**Tray menu** (right-click the icon):

- **Show shortcuts…** — list every loaded shortcut
- **Reload snippets** — re-read the snippet files after you add or edit any
- **Exit**

> **Note:** expansion only fires while **SSMS is the focused window**, so your
> shortcuts never trigger in other apps.

---

## Shortcut reference

| Shortcut | Expands to |
|----------|-----------|
| `ssf` | `SELECT * FROM ⎸ WITH (NOLOCK)` |
| `st10` / `st100` / `st1000` | `SELECT TOP N * FROM [TableName]` |
| `sc` | `SELECT COUNT(*) FROM [TableName]` |
| `sd` | `SELECT DISTINCT col FROM [TableName]` |
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

---

## Adding or editing shortcuts

Shortcuts are plain **`.snippet` XML files** (the standard SSMS/Visual Studio format)
in the [`snippets/`](snippets/) folder. To add one, copy an existing file and change:

- `<Shortcut>` — the code you type before Tab
- `<Code>` — the T-SQL, where:
  - `$end$` marks where the cursor should land
  - `$LiteralID$` is a field; its `<Default>` (from `<Declarations>`) is inserted and,
    if it's the first field, **selected** after expansion so you can type over it

Example (`snippets/ssf.snippet`):

```xml
<Code Language="SQL"><![CDATA[SELECT * FROM $end$ WITH (NOLOCK)]]></Code>
```

After editing, re-run `.\Install.ps1 -NoBuild` (or copy the file into
`Documents\SQL Server Management Studio*\Snippets\My Shortcuts\`), then choose
**Reload snippets** from the tray menu.

---

## How it works

- Installs low-level **keyboard** and **mouse** hooks (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`).
- Only acts when the foreground process is SSMS (cached lookup keeps the hook fast so
  Windows never drops it for exceeding `LowLevelHooksTimeout`).
- Buffers typed letters/digits as the "current word"; resets on space, Enter, arrows,
  a mouse click, or a 2-second pause.
- On a matching **Tab**: suppresses the Tab, sets the clipboard to the expansion,
  sends backspaces to erase the shortcut, pastes with `Ctrl+V`, then sends
  Left / Shift+Left keystrokes to position the caret (or select the first field), and
  restores your previous clipboard.
- Snippet files are loaded from both your physical `Documents` folder and a
  OneDrive-redirected one, so it works regardless of Known Folder redirection.

Diagnostic logging is **off by default**; run with `--debug` to write
`%TEMP%\SsmsSnippetExpander.log` for troubleshooting.

---

## Limitations

- **No Tab-through of multiple fields.** The caret jumps to (and selects) the *first*
  field; it cannot Tab from one field to the next — that requires a real SSMS add-in.
  If you need full field navigation and an IntelliSense dropdown, consider
  [dbForge SQL Complete Express](https://www.devart.com/dbforge/sql/sqlcomplete/) (free).
- The "current word" is tracked by keystroke, not read from the editor, so very fast
  typing across a boundary can occasionally mis-capture. The resets above cover the
  common cases.
- If an identifier you type happens to equal a shortcut (e.g. a column named `df`),
  `Tab` will expand it — press `Ctrl+Z` to undo.

---

## License

[MIT](LICENSE)
