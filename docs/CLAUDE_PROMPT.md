# Prompt for Claude — Continue the "SSMS Snippet Expander" Project (v1 — superseded)

> **⚠️ Superseded.** This is the original handoff prompt, kept for history. Use
> [`CLAUDE_PROMPT_V2.md`](CLAUDE_PROMPT_V2.md) instead — it reflects the current code (embedded
> snippets, atomic `SendInput`, caret positioning, `--debug` logging). For the full history and
> session log see [`DEVELOPMENT.md`](DEVELOPMENT.md). The text below has been corrected for
> accuracy but V2 is the one to paste.

Paste everything below (from the `---` line down) into Claude as your message. It tells Claude
what we're building, what already exists, every technical detail, and how to continue.

---

You are helping me continue a small Windows tool I already started building with Claude. Read this
entire brief first, then wait for my specific request. Do not rewrite things that already work.

## What I'm trying to achieve

I want **Redgate SQL Prompt–style snippet expansion in SQL Server Management Studio (SSMS) 22**:
I type a short code and press **Tab**, and it expands to a full T-SQL statement. Example:
`ssf` + Tab → `SELECT * FROM ⎸ WITH (NOLOCK)`; `st100` + Tab → `SELECT TOP 100 * FROM [TableName]`.
I do **not** own SQL Prompt and want this behavior for free.

## Critical constraint we already discovered

SSMS's SQL editor **ignores the `<Shortcut>` element** in native `.snippet` XML — unlike Visual
Studio, it does **not** support type-shortcut-then-Tab. Native snippets in SSMS can only be
inserted via the `Ctrl+K, Ctrl+X` menu picker. SQL Prompt only gets tab-expansion because it's a
custom add-in. **Therefore we built a standalone tray app that does the expansion via a global
keyboard hook.** Do not suggest "just use SSMS snippets" — we proved that doesn't tab-expand.

## What already exists

### 32 snippet XML files
They live in the repo under `snippets/*.snippet` and are **embedded into the exe**; at runtime the
app also reads on-disk copies (which override/extend the built-ins) from the physical, NOT
OneDrive-redirected, path:
`C:\Users\<user>\Documents\SQL Server Management Studio 22\Snippets\My Shortcuts\*.snippet`
Standard Microsoft Code Snippet format (`Format="1.0.0"`), with `<Shortcut>`, `<Literal>` defaults,
and `<Code>` containing `$LiteralID$` placeholders plus `$end$`. Shortcuts include:
`ssf, st10, st100, st1000, sc, sd, ij, lj, loj, rj, roj, cj, foj, gb, ob, hav, ii, df, uf, cp, ap,
cv, cf, af, cte, tr, wl, ifex, ifnex, cdb, isnl, isn`.

### The expander app
- Project: `C:\Users\<user>\source\repos\SsmsSnippetExpander\` — single-file .NET 8 WinForms tray
  app (`net8.0-windows`, `WinExe`, `AllowUnsafeBlocks=true` for source-generated `LibraryImport`).
- Source: `Program.cs` (explicit `static class Program` with a `[STAThread] Main` + `partial class
  MainForm` + a `record Snippet`; not top-level statements — the entry point needs `[STAThread]`).
- Output: `bin\Release\net8.0-windows\SsmsSnippetExpander.exe`.

How it works:
1. Installs a global low-level keyboard hook (`WH_KEYBOARD_LL`) and mouse hook (`WH_MOUSE_LL`).
2. When the foreground process is SSMS (process name `SSMS`), it buffers typed letters/digits as
   the "current word."
3. On **Tab**, if the buffer matches a shortcut, it: suppresses the Tab (`return 1`), saves the
   clipboard, sets the expansion text, then sends backspaces + `Ctrl+V` as one atomic `SendInput`
   batch, moves the caret to `$end$` (or selects the first literal) with Left / Shift+Left, and
   restores the clipboard after ~600 ms.
4. Runs invisibly with a system-tray icon (Show shortcuts / Reload / Exit).
5. Writes a diagnostic log to `%TEMP%\SsmsSnippetExpander.log`.

## Bugs already found and fixed (do NOT reintroduce)

1. Missing `app.ico` → removed `<ApplicationIcon>`.
2. `LibraryImport` needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
3. Multiple instances double/triple-pasted → added `Global\` **Mutex** single-instance guard.
4. `Process.GetProcessById` per keystroke inside the LL hook risked the ~300 ms
   `LowLevelHooksTimeout` (Windows silently drops slow hooks) and lagged typing → **cache** the
   foreground-window → isSSMS result; only re-resolve when the foreground HWND changes.
5. `Invoke()` threw because the hidden form's handle was never created → force `_ = Handle;` in ctor.
6. `Clipboard.SetText` can throw when the clipboard is locked → retry 5× / 20 ms, abort cleanly.
7. `GetKeyState` unreliable in a global hook → use `GetAsyncKeyState`.
8. Doubled `$$` in the first 4 generated snippet files (`[$$TableName$$]`) → fixed to `[$TableName$]`.
9. **Main "nothing happens" bug:** `Environment.SpecialFolder.MyDocuments` pointed at the
   **OneDrive-redirected** Documents (`...\OneDrive - empeon.com\Documents`), but SSMS + the files
   live in the **physical** `C:\Users\<user>\Documents` → loader found `snippets=0`. **Fix:** scan
   BOTH `%USERPROFILE%\Documents` and the redirected `MyDocuments`, de-duplicated.
10. Stale buffer: log showed `typed='nssf'` (a stray `n` glued to `ssf`) → added **idle reset**
    (clear buffer if >2 s since last keystroke) and **mouse-click reset** (clear on any click).

## Environment facts
Windows 11, PowerShell 7+. SSMS 22 at `C:\Program Files\Microsoft SQL Server Management Studio 22\`
(v21 also present); it's the 64-bit VS-shell build; process name `SSMS`. SSMS and the app both run
non-elevated (elevation/UIPI ruled out). .NET SDK 10 present; project targets `net8.0-windows`.

## Build / run / diagnostics
```powershell
cd "$env:USERPROFILE\source\repos\SsmsSnippetExpander"
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\SsmsSnippetExpander.exe"   # tray app, no window
Stop-Process -Name "SsmsSnippetExpander" -Force
Get-Content "$env:TEMP\SsmsSnippetExpander.log"
```

## Current status
Working: 32 snippets load, keyboard+mouse hooks installed, expansion pastes via clipboard, idle +
click buffer resets in place. The diagnostic log confirmed the hook detects Tab in SSMS and matches
correctly.

## Known limitations / likely next requests
- The caret is repositioned to `$end$` and the first literal is selected, but it can **not** Tab
  between multiple `$Literal$` fields (native menu insertion does). Full Tab-through is the main
  remaining UX gap.
- Buffer is a heuristic token tracker, not a real editor read.
- Clipboard restore is time-based (~600 ms) and text-only — racy if paste is slow or you copy
  during the window.
- No installer, not code-signed; runs from the build folder (`Install.ps1` handles per-user
  copy/build/startup).

Already done since this prompt was first written: `--debug`-gated logging, atomic `SendInput`
(replacing `keybd_event`), and caret positioning at `$end$` / first-literal selection.

## My request
<Describe what you want next — e.g. "add caret positioning at $end$", "let me Tab between literal
placeholders", "add/edit these shortcuts: …", "make a signed installer", "remove the diagnostic
logging", or "it still doesn't expand — read the log and diagnose.">
