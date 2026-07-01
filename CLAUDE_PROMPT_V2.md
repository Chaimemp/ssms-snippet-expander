# Full Prompt for Claude — SSMS Snippet Expander (v2, code-reviewed)

Paste everything from the `---` line down into Claude as your message. Unlike the earlier
handoff, this version was written *after* reading the actual `Program.cs`, comparing it to the
open-source alternatives, and identifying concrete improvements and failure modes. Fill in the
"My request" block at the bottom before sending.

---

You are helping me continue a small Windows tool. **Read this entire brief before doing anything,
then wait for my specific request at the bottom.** The code already works in principle — do not
rewrite what works; improve or diagnose only what I ask for.

## 1. Goal

Replicate Redgate SQL Prompt's tab-expansion in SQL Server Management Studio (SSMS) 22, for free:
type a short code, press **Tab**, get a full T-SQL statement.
Example: `ssf` + Tab → `SELECT * FROM [TableName]`; `st100` + Tab → `SELECT TOP 100 * FROM [TableName]`.

## 2. The constraint we already proved (do not re-litigate)

SSMS's SQL editor **ignores the `<Shortcut>` element** in native `.snippet` XML. That element is
honored by Visual Studio, not by SSMS — confirmed by our own testing and by public sources
(SQLServerCentral, community threads). In SSMS, native snippets can only be inserted via the
`Ctrl+K, Ctrl+X` menu picker. SQL Prompt gets tab-expansion only because it's a custom add-in.
**So we built a standalone tray app that does the expansion via a global keyboard hook.** Do not
suggest "just use SSMS snippets" — that path is closed.

## 3. What exists (verified from source)

**Project:** `C:\Users\<user>\source\repos\SsmsSnippetExpander\`
Single-file .NET 8 WinForms tray app.
- `Program.cs` — top-level statements + `partial class MainForm` (~408 lines).
- `SsmsSnippetExpander.csproj` — `net8.0-windows`, `WinExe`, `UseWindowsForms=true`,
  `Nullable=enable`, `AllowUnsafeBlocks=true`, `ImplicitUsings=enable`.
- Output: `bin\Release\net8.0-windows\SsmsSnippetExpander.exe`.
- The **32 `.snippet` files are NOT in the repo** — they live under
  `Documents\SQL Server Management Studio*\Snippets\My Shortcuts\`. The app parses them at
  startup. (Repo grep for `*.snippet` returns 0 — this is expected, not a bug.)

**How the code actually works (confirmed):**
1. `Global\` **Mutex** single-instance guard; second launch shows a message box and exits.
2. Installs `WH_KEYBOARD_LL` and `WH_MOUSE_LL` low-level hooks; keeps delegate refs alive to
   prevent GC.
3. Hot path `HookProc`: ignores injected keys (`LLKHF_INJECTED`) to avoid feeding on its own
   backspaces/paste; only acts when `IsSsmsFocused()`.
4. `IsSsmsFocused()` caches the `GetForegroundWindow()` HWND → isSSMS result and only calls the
   expensive `Process.GetProcessById` when the HWND changes. Match is
   `ProcessName.StartsWith("Ssms", OrdinalIgnoreCase)`.
5. Buffer accumulates A–Z / 0–9 only. **Idle reset** clears it if >2 s since last key.
   Backspace pops one char. Any other key (space, Enter, arrows, unmatched Tab) clears it.
   A **mouse click** clears it (caret moved).
6. On Tab with a matched buffer: returns `1` to suppress the Tab, then (after a 20 ms delay)
   `Invoke`s `Expand(len, expansion)` on the UI thread.
7. `Expand`: saves clipboard text → sets expansion text (retry 5× / 20 ms) → sends `deleteCount`
   backspaces via `keybd_event` → sends `Ctrl+V` → restores the old clipboard after 600 ms.
8. Loader scans **both** `%USERPROFILE%\Documents` and the OneDrive-redirected `MyDocuments`,
   de-duplicated; parses each `.snippet`, replaces `$LiteralID$` with its `<Default>`, strips
   `$end$`/`$selected$`, and stores `shortcut → code` in a case-insensitive dictionary.
9. Tray icon: "N snippets loaded", Show shortcuts…, Reload snippets, Exit. Diagnostic log at
   `%TEMP%\SsmsSnippetExpander.log`.

## 4. Bugs already fixed — do NOT reintroduce

1. Removed missing `<ApplicationIcon>` (build error CS7064).
2. Added `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for source-generated `LibraryImport`.
3. `Global\` Mutex to stop multiple instances triple-pasting.
4. Cache foreground→isSSMS check so the LL hook never blows the ~300 ms `LowLevelHooksTimeout`.
5. Force `_ = Handle;` in ctor so `Invoke()` works on the never-shown form.
6. `TrySetClipboardText` retries 5× / 20 ms when the clipboard is locked.
7. Use `GetAsyncKeyState`, not `GetKeyState`, inside the hook.
8. Fixed doubled `$$` in the first 4 generated snippet files.
9. **The main "nothing happens" bug:** OneDrive-redirected Documents → `snippets=0`. Fixed by
   scanning both physical and redirected Documents.
10. Stale buffer (`typed='nssf'`) → added idle reset + mouse-click reset.

## 5. How this compares to what others have done (open source)

I researched prior art. Findings:
- The limitation is real and widely reported; SSMS just doesn't do shortcut+Tab.
- The common free workaround is **AutoHotkey**, not a custom app. The closest public project,
  `ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet`, is essentially six one-line hotstrings:
  ```ahk
  ::ssf::select * from
  ::sst::select top 100 * from
  ::sd::select distinct
  ::dt::drop table
  ::ij::inner join
  ::tt::truncate table
  ```
  It triggers on the **end char (space/Enter)**, is global (not scoped to SSMS), has no literal
  substitution, no clipboard preservation, no caret handling, and no single-instance/hook-timeout
  concerns. **Our app is significantly more capable and more robust than the published options.**
- General-purpose expanders (espanso, TextExpander-likes) already solve the global-hook +
  clipboard-paste + per-app-scoping problems and support caret positioning — worth knowing as a
  fallback, but they don't parse SSMS `.snippet` XML.
- **No published open-source project matches ours** (SSMS-scoped .NET tray app, LL hook, snippet
  XML parsing). The one thing SQL Prompt does that none of the free routes do is **tab-through of
  literal placeholders** — that is the genuine remaining gap.

## 5b. Reference sources — study these for ideas before proposing changes

When I ask for an improvement (especially caret handling, triggering, or expansion behavior),
**first look at how others solved similar problems and borrow good ideas.** Don't copy blindly —
our app is more capable than most of these — but use them for technique and edge-case awareness:

- **`ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet`** (GitHub, `master` branch) — the closest
  direct analog. Read its `.ahk` script. It's minimal (end-char-triggered hotstrings, no literal
  substitution), but it shows the simplest viable trigger model and which shortcuts people actually
  want. URL: https://github.com/ovnisoftware/Autohotkey-SSF-SQL-Code-Snippet
- **AutoHotkey community threads on SSMS hotstrings** — for context-sensitive scoping
  (`#IfWinActive`), the `ahk_class HwndWrapper` window-matching quirk, and the elevation/UIPI
  gotcha (script must run at SSMS's integrity level). Search "AutoHotkey SSMS hotstring".
- **espanso** (open-source cross-platform text expander) — for how a mature tool handles global
  hooks, clipboard-based paste, per-app scoping, and especially **cursor positioning** and
  form/field prompts. Good model for the `$end$` / literal tab-through work.
- **Redgate SQL Prompt docs** — for the target UX we're emulating (which literals it prompts for,
  Tab-through order) so our behavior matches user muscle memory.
- **Microsoft VS Code Snippet XML schema** — canonical meaning of `$end$`, `$selected$`, and
  `$LiteralID$` so our parser/caret logic stays spec-correct.

If a source is reachable, actually fetch and read it rather than guessing. Summarize what you
borrowed and why, and call out anything you deliberately did NOT adopt.

## 6. Concrete improvement opportunities (from reading the code)

Ordered roughly by value. Don't do these unless I ask — this is the menu.

1. **Caret positioning at `$end$` / literal tab-through (biggest UX gap).** Today the loader strips
   `$end$` and inlines literal defaults, so the caret lands at the end of the pasted text. Options,
   easiest → hardest:
   - Record the offset of `$end$` within the expansion; after paste, send Left-arrow keystrokes to
     move the caret back to that offset.
   - Select the first literal's default so the user can type over it.
   - Full SQL Prompt–style Tab-through of multiple literals (requires tracking field state and
     intercepting subsequent Tabs — meaningful work).
2. **Gate the diagnostic logging.** `Log(...)` writes on every Tab unconditionally. Put it behind a
   `--debug` arg or a compile-time `#if DEBUG` / env-var flag so release builds are quiet.
3. **Replace `keybd_event` with `SendInput`.** `keybd_event` is legacy; `SendInput` is atomic and
   less prone to interleaving with real user input. Low risk, modernizes the input path.
4. **Clipboard restore is time-based (600 ms) — racy.** If paste is slow or the user copies during
   the window, the wrong thing can land. Consider using the Win32 clipboard-sequence-number or a
   delayed-render approach, or at least make the delay configurable.
5. **Buffer is a heuristic, not an editor read.** Fine for now; note that rapid typing across a
   boundary can still mis-capture. A true fix would read the editor token, which the LL-hook design
   can't do — would need UI Automation (heavier).
6. **Packaging:** no installer, not code-signed, runs from the build folder. A Startup-shortcut
   script exists in the old handoff. A signed single-file publish (`dotnet publish -c Release
   -r win-x64 -p:PublishSingleFile=true`) + a small installer would make this shareable.
7. **Process-name match is a prefix (`StartsWith("Ssms")`).** Verify SSMS 21/22 (the VS-shell
   rebuild) still reports process name `Ssms`. If a future SSMS renames the process, matching
   breaks silently — consider matching on window class or main-module path as a backup.

## 7. If it's "not working right now" — diagnostic order

Work top-down; each step tells you where it breaks.
1. **Is the app running?** Look for the tray icon. If not, it isn't autostarted — launch the exe.
   `Get-Process SsmsSnippetExpander`.
2. **Read the log:** `Get-Content "$env:TEMP\SsmsSnippetExpander.log"`.
   - Startup line shows `snippets=N`. If `N=0` → loader found no files (OneDrive path / files
     missing / wrong folder). Check `Documents\SQL Server Management Studio*\Snippets\My Shortcuts`.
   - `TAB in SSMS. typed='...', match=False` → the hook and focus work; the captured word just
     isn't a known shortcut (stale prefix, or typo). Retype cleanly.
   - **No `TAB in SSMS` line at all** when you press Tab in SSMS → either focus isn't detected
     (process-name mismatch) or the hook was dropped (timeout / another LL hook / elevation).
3. **Elevation mismatch (UIPI):** if SSMS runs **as administrator** and the expander does **not**
   (or vice-versa), the hook can't see/inject into SSMS. Run both at the same integrity level.
4. **Rebuild fresh** and confirm you're running the new exe:
   `dotnet build -c Release` then relaunch from `bin\Release\net8.0-windows\`.
5. **Only one instance:** `Stop-Process -Name SsmsSnippetExpander -Force` then start once.

Note: I (Claude) am running in a sandbox and **cannot see your live tray process or your
`%TEMP%` log** — paste the log contents into the chat and I'll interpret them.

## 8. Build / run / diagnostics

```powershell
cd "$env:USERPROFILE\source\repos\SsmsSnippetExpander"
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\SsmsSnippetExpander.exe"   # tray app, no window
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
- "Add caret positioning at $end$ (option 1 from section 6) and nothing else."
- "Gate the logging behind a --debug flag and switch keybd_event to SendInput."
- "It's not expanding. Here's my log: <paste>. Diagnose it."
- "Add/edit these shortcuts: …"
- "Make a signed single-file installer.">
