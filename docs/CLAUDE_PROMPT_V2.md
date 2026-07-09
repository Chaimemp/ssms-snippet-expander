# Full Prompt for Claude — SSMS Snippet Expander (v2 — superseded)

> **⚠️ Superseded.** Kept for history. Use [`CLAUDE_PROMPT_V3.md`](CLAUDE_PROMPT_V3.md)
> instead — it covers the F12/Ctrl+F12 features and the SSMS 22 VSIX extension.

Paste everything from the `---` line down into a fresh Claude session as your message. This is the
**single authoritative "continue this project" prompt** — it was written after reading the actual
`Program.cs` and reflects the code as it stands today (post code-review pass). For the long-form
history (every bug, the session log), point Claude at [`DEVELOPMENT.md`](DEVELOPMENT.md); for the
user-facing overview see [`../README.md`](../README.md). Fill in the **"My request"** block at the
bottom before sending.

---

You are helping me continue a small Windows tool. **Read this entire brief before doing anything,
then wait for my specific request at the bottom.** The code already works — do not rewrite what
works; improve or diagnose only what I ask for. The repo builds clean with `dotnet build -c Release`.

## 1. Goal

Replicate Redgate SQL Prompt's tab-expansion in SQL Server Management Studio (SSMS) 22, for free:
type a short code, press **Tab**, get a full T-SQL statement.
Examples: `ssf` + Tab → `SELECT * FROM ⎸ WITH (NOLOCK)` (caret lands between `FROM` and `WITH`);
`st100` + Tab → `SELECT TOP 100 * FROM [TableName]` (with `TableName` selected so you type over it).

## 2. The constraint we already proved (do not re-litigate)

SSMS's SQL editor **ignores the `<Shortcut>` element** in native `.snippet` XML. That element is
honored by Visual Studio, not by SSMS — confirmed by our own testing and by public sources
(SQLServerCentral, community threads). In SSMS, native snippets can only be inserted via the
`Ctrl+K, Ctrl+X` menu picker. SQL Prompt gets tab-expansion only because it's a custom add-in.
**So we built a standalone tray app that does the expansion via a global keyboard hook.** Do not
suggest "just use SSMS snippets" — that path is closed.

## 3. What exists (verified from source)

**Project:** `C:\Users\<user>\source\repos\SsmsSnippetExpander\` — a single-file .NET 8 WinForms
tray app. Public repo: `https://github.com/Chaimemp/ssms-snippet-expander`.

- `Program.cs` (~650 lines) — an explicit `static class Program` with a `[STAThread] Main`
  (STA is required for the WinForms/OLE clipboard), a `partial class MainForm : Form`, and a
  `sealed record Snippet(string Text, int LeftMoves, int SelectLen)`. **Not** top-level statements —
  the entry point needs `[STAThread]`, which top-level statements can't mark.
- `SsmsSnippetExpander.csproj` — `net8.0-windows`, `WinExe`, `UseWindowsForms=true`,
  `Nullable=enable`, `ImplicitUsings=enable`, `AllowUnsafeBlocks=true` (source-generated
  `LibraryImport` P/Invoke requires it). Also sets `Version`/`Product`/`Description`/`Authors`.
- The **32 `.snippet` files live in the repo** under `snippets/` and are **embedded into the exe**
  (`<EmbeddedResource Include="snippets\*.snippet" />`) so a standalone download works before any
  files are copied to the SSMS folder. `Install.ps1` also copies them into
  `Documents\SQL Server Management Studio*\Snippets\My Shortcuts\`.
- `Install.ps1` — copies snippets + builds; `-Startup` registers a login shortcut; `-NoBuild`
  copies only. `README.md`, `LICENSE` (MIT), and `docs/` are also in the repo.
- Output: `bin\Release\net8.0-windows\SsmsSnippetExpander.exe`.

**How the code actually works (confirmed):**
1. `Global\` **Mutex** single-instance guard; a second launch shows a message box and exits.
2. `--debug` arg turns on diagnostic logging to `%TEMP%\SsmsSnippetExpander.log` (off otherwise).
3. Installs `WH_KEYBOARD_LL` and `WH_MOUSE_LL` low-level hooks; keeps the delegate refs alive to
   prevent GC. The hidden form forces `_ = Handle;` in the ctor so `Invoke()` works.
4. Hot path `HookProc`: ignores injected keys (`LLKHF_INJECTED`) so it doesn't feed on its own
   backspaces/paste; ignores modifier keydowns; only acts when `IsSsmsFocused()`.
5. `IsSsmsFocused()` caches the `GetForegroundWindow()` HWND → isSSMS result and only calls the
   expensive `Process.GetProcessById` when the HWND changes. Match is
   `ProcessName.StartsWith("Ssms", OrdinalIgnoreCase)`.
6. Buffer accumulates A–Z, top-row digits, and **numpad digits**. **Idle reset** clears it if >2 s
   since the last key. Backspace pops one char. **Alt/Ctrl chords** (Ctrl+S, Alt+Tab, …) and
   **Shift+digit** (types a symbol) clear it and pass through. **Shift+Tab** (unindent) does not
   expand. Any other key (space, Enter, arrows, unmatched Tab) clears it. A **mouse click** clears
   it (the caret moved).
7. On Tab with a matched buffer: returns `1` to suppress the Tab, then (after ~20 ms) `Invoke`s
   `Expand(len, snippet)` on the UI thread.
8. `Expand`: saves the current clipboard text → sets the expansion text (retry 5× / 20 ms if the
   clipboard is locked) → sends `deleteCount` backspaces **and** `Ctrl+V` as **one atomic
   `SendInput` batch** → after ~70 ms, sends Left / Shift+Left as a second atomic batch to move the
   caret to `$end$` or select the first literal → restores the old clipboard after ~600 ms.
9. Loader (`LoadSnippets`): first reads the **embedded** built-in `.snippet` resources, then scans
   **both** `%USERPROFILE%\Documents` and the OneDrive-redirected `MyDocuments` (de-duplicated) for
   on-disk `.snippet` files that **override/extend** the built-ins. Each file: replace `$LiteralID$`
   with its `<Default>`, strip `$end$`/`$selected$` (recording caret offsets), normalize newlines to
   `\n`, store `shortcut → Snippet` in a case-insensitive dictionary.
10. Tray icon: "N snippets loaded" (disabled label), Show shortcuts…, Reload snippets, Exit;
    double-click shows the shortcut list; a balloon reports the active count on start/reload.

## 4. Bugs already fixed — do NOT reintroduce

1. Removed a missing `<ApplicationIcon>` (build error CS7064); the app uses `SystemIcons.Application`.
2. Added `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for source-generated `LibraryImport`. (This
   has regressed before — it is required; do not remove it as "unused".)
3. `Global\` Mutex to stop multiple instances triple-pasting.
4. Cache the foreground→isSSMS check so the LL hook never blows the ~300 ms `LowLevelHooksTimeout`.
5. Force `_ = Handle;` in the ctor so `Invoke()` works on the never-shown form.
6. `TrySetClipboardText` retries 5× / 20 ms when the clipboard is locked, then aborts cleanly.
7. Use `GetAsyncKeyState`, not `GetKeyState`, inside the hook.
8. Fixed doubled `$$` in the first 4 generated snippet files; `$$` now escapes to a literal `$`.
9. **The main "nothing happens" bug:** OneDrive-redirected Documents → `snippets=0`. Fixed by
   scanning both physical and redirected Documents.
10. Stale buffer (`typed='nssf'`) → idle reset + mouse-click reset.
11. **Alt+Tab / Ctrl chords used to expand or corrupt the buffer** → chord detection clears and
    passes through. **Shift keydown / Shift+digit / Shift+Tab** edge cases handled. Numpad digits
    now feed the buffer. `keybd_event` replaced with atomic `SendInput`. Logging gated behind
    `--debug`.

## 5. How this compares to prior art (open source)

- The limitation is real and widely reported; SSMS just doesn't do shortcut+Tab.
- The common free workaround is **AutoHotkey**, not a custom app. The closest public project,
  `ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet`, is ~six one-line hotstrings
  (`::ssf::select * from`, …): end-char-triggered, global (not scoped to SSMS), no literal
  substitution, no clipboard preservation, no caret handling, no single-instance/hook-timeout care.
- General-purpose expanders (espanso, TextExpander-likes) solve global-hook + clipboard-paste +
  per-app scoping and support caret positioning — a useful fallback, but they don't parse SSMS
  `.snippet` XML.
- **No published open-source project matches ours** (SSMS-scoped .NET tray app, LL hook, snippet XML
  parsing, caret positioning). The one thing SQL Prompt does that we don't is **Tab-through of
  multiple literal placeholders** — that is the genuine remaining gap.

## 5b. Reference sources — study these before proposing changes

When I ask for an improvement (especially caret handling, triggering, or expansion behavior),
**first look at how others solved similar problems and borrow good ideas** — don't copy blindly:

- **`ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet`** — closest analog; shows the simplest viable
  trigger model and which shortcuts people want. `https://github.com/ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet`
- **AutoHotkey SSMS-hotstring threads** — `#IfWinActive` scoping, `ahk_class HwndWrapper` matching,
  and the elevation/UIPI gotcha (script must run at SSMS's integrity level).
- **espanso** — mature model for global hooks, clipboard-paste, per-app scoping, and cursor
  positioning / field prompts. Good reference for the literal tab-through work.
- **Redgate SQL Prompt docs** — the target UX (which literals it prompts for, Tab-through order).
- **Microsoft Code Snippet XML schema** — canonical meaning of `$end$`, `$selected$`, `$LiteralID$`.

If a source is reachable, actually fetch and read it. Summarize what you borrowed and why, and call
out anything you deliberately did NOT adopt.

## 6. Improvement opportunities (the menu — don't do these unless I ask)

**Already done** (do not "add" these — they exist): caret positioning at `$end$` and first-literal
selection (§3 step 8); `--debug`-gated logging; `keybd_event` → atomic `SendInput`; `Install.ps1`
with an optional login-startup shortcut.

Remaining, roughly by value:
1. **Tab-through of multiple literals (biggest remaining UX gap).** We select the *first* literal or
   land at `$end$`, but can't Tab from one field to the next. Requires tracking field state and
   intercepting subsequent Tabs — meaningful work.
2. **Clipboard restore is time-based (~600 ms) — racy.** If paste is slow or the user copies during
   the window, the wrong thing can land. Consider the Win32 clipboard-sequence-number, a
   delayed-render approach, or at least a configurable delay.
3. **Buffer is a heuristic, not an editor read.** Rapid typing across a boundary can still
   mis-capture. A true fix would read the editor token (UI Automation — heavier; the LL-hook design
   can't do it).
4. **Non-QWERTY layouts.** Letter mapping assumes VK ≈ ASCII; non-Latin layouts may mis-capture.
5. **Packaging / distribution.** Not code-signed. A signed single-file publish
   (`dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true`) + a small installer would make
   it shareable. `Install.ps1` already handles per-user copy/build/startup.
6. **Process-name match is a prefix (`StartsWith("Ssms")`).** If a future SSMS renames its process,
   matching breaks silently — consider a window-class or main-module-path backup.

## 7. If it's "not working right now" — diagnostic order

Work top-down; each step tells you where it breaks.
1. **Is the app running?** Look for the tray icon. `Get-Process SsmsSnippetExpander`. If not, launch
   the exe (it isn't autostarted unless you ran `Install.ps1 -Startup`).
2. **Read the log** (run the exe with `--debug` first): `Get-Content "$env:TEMP\SsmsSnippetExpander.log"`.
   - Startup line shows `snippets=N`. `N=0` → loader found no files (should not happen now that
     built-ins are embedded — if it does, the embedded resources didn't ship; rebuild).
   - `TAB in SSMS. typed='...', match=False` → hook + focus work; the captured word just isn't a
     known shortcut (stale prefix or typo). Retype cleanly.
   - **No `TAB in SSMS` line at all** when you press Tab in SSMS → focus not detected (process-name
     mismatch) or the hook was dropped (timeout / another LL hook / elevation).
3. **Elevation mismatch (UIPI):** if SSMS runs **as administrator** and the expander does not (or
   vice-versa), the hook can't see/inject into SSMS. Run both at the same integrity level.
4. **Rebuild fresh** and confirm you're running the new exe: `dotnet build -c Release`, relaunch from
   `bin\Release\net8.0-windows\`.
5. **Only one instance:** `Stop-Process -Name SsmsSnippetExpander -Force`, then start once.

Note: I (Claude) run in a sandbox and **cannot see your live tray process or your `%TEMP%` log** —
paste the log contents (run with `--debug`) into the chat and I'll interpret them.

## 8. Build / run / diagnostics

```powershell
cd "$env:USERPROFILE\source\repos\SsmsSnippetExpander"
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\SsmsSnippetExpander.exe" -ArgumentList '--debug'   # tray app, no window
Stop-Process -Name "SsmsSnippetExpander" -Force
Get-Content "$env:TEMP\SsmsSnippetExpander.log"
```

## 9. Environment facts

Windows 11 Pro, PowerShell 7+. SSMS 22 at
`C:\Program Files\Microsoft SQL Server Management Studio 22\` (v21 also present); 64-bit VS-shell
build; process name `Ssms`. Both SSMS and the app run non-elevated. .NET SDK 10 present; project
targets `net8.0-windows`.

## 10. My request

<Replace this with what you want. Examples:
- "Add Tab-through of multiple literals (item 1 in section 6) and nothing else."
- "Make the clipboard-restore delay configurable / fix the race (item 2)."
- "It's not expanding. Here's my --debug log: <paste>. Diagnose it."
- "Add/edit these shortcuts: …"
- "Make a signed single-file publish + installer.">
