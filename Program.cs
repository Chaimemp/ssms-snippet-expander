using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    // WinForms clipboard uses OLE, which REQUIRES a single-threaded apartment.
    // Top-level statements don't mark the entry point [STAThread], so we use an
    // explicit STA Main — otherwise Clipboard.SetText throws and nothing pastes.
    [STAThread]
    static void Main(string[] args)
    {
        // Diagnostic logging is opt-in (pass --debug) so end users get a quiet app.
        MainForm.DebugLogging = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, @"Global\SsmsSnippetExpander_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("SSMS Snippet Expander is already running (see the system tray).",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new MainForm());
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show($"{ex.Message} The app will exit.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

partial class MainForm : Form
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    const int  WH_KEYBOARD_LL  = 13;
    const int  WH_MOUSE_LL      = 14;
    const nint WM_KEYDOWN       = 0x0100;
    const nint WM_SYSKEYDOWN    = 0x0104;
    const nint WM_LBUTTONDOWN   = 0x0201;
    const nint WM_RBUTTONDOWN   = 0x0204;
    const nint WM_MBUTTONDOWN   = 0x0207;
    const long IDLE_RESET_MS    = 2000;
    const int  VK_TAB           = 0x09;
    const int  VK_BACK          = 0x08;
    const int  VK_SHIFT         = 0x10;
    const int  VK_CONTROL       = 0x11;
    const int  VK_CAPITAL       = 0x14;
    const int  VK_LEFT          = 0x25;
    const int  VK_RIGHT         = 0x27;
    const int  VK_C             = 0x43;
    const int  VK_N             = 0x4E;
    const int  VK_V             = 0x56;
    const int  VK_F8            = 0x77;
    const int  VK_F12           = 0x7B;
    const int  LLKHF_INJECTED   = 0x10;
    const int  LLKHF_ALTDOWN    = 0x20;
    const uint KEYEVENTF_KEYUP  = 0x0002;
    const uint INPUT_KEYBOARD   = 1;
    const int  MAX_BUFFER       = 32;

    // Timings (ms): let the suppressed Tab settle before pasting; let SSMS finish
    // pasting before moving the caret; hold the clipboard long enough to paste.
    const int  EXPAND_DELAY_MS      = 20;
    const int  CARET_SETTLE_MS      = 70;
    const int  CLIPBOARD_RESTORE_MS = 600;

    delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int id, LowLevelKeyboardProc cb, nint hMod, uint tid);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wp, nint lp);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, ReadOnlySpan<INPUT> pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public nint   dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int  dx;
        public int  dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    // MOUSEINPUT is the largest union member — including it sizes INPUT correctly.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint       type;
        public InputUnion u;
    }

    // ── State ────────────────────────────────────────────────────────────────
    readonly NotifyIcon                 _tray;
    readonly ToolStripMenuItem          _menuInfo;
    readonly nint                       _hook;
    readonly nint                       _mouseHook;
    readonly LowLevelKeyboardProc       _proc;        // keep refs — prevent GC collection
    readonly LowLevelKeyboardProc       _mouseProc;
    readonly Dictionary<string, Snippet> _snippets;
    readonly StringBuilder              _buffer = new();
    long                                _lastKeyTick;

    // Cached foreground-window check — recomputed only when the window changes,
    // so the hook never calls Process.GetProcessById on the hot path (LL hooks
    // that exceed LowLevelHooksTimeout are silently removed by Windows).
    nint _cachedHwnd;
    bool _cachedIsSsms;

    internal static bool DebugLogging;

    static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "SsmsSnippetExpander.log");

    static void Log(string msg)
    {
        if (!DebugLogging) return;
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
        catch { }
    }

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainForm()
    {
        ShowInTaskbar   = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState     = FormWindowState.Minimized;
        Size            = new Size(1, 1);

        // Force the window handle to exist now, so Invoke() works even though
        // the form is never made visible.
        _ = Handle;

        _snippets = LoadSnippets();

        var menu = new ContextMenuStrip();
        _menuInfo = (ToolStripMenuItem)menu.Items.Add($"{_snippets.Count} snippets loaded");
        _menuInfo.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show shortcuts…", null, (_, _) => ShowStatus());
        menu.Items.Add("Reload snippets",       null, OnReload);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = TrayText(),
            ContextMenuStrip = menu,
            Visible          = true
        };
        _tray.MouseDoubleClick += (_, _) => ShowStatus();

        nint hMod = GetModuleHandleW(null);
        _proc      = HookProc;
        _mouseProc = MouseProc;
        _hook      = SetWindowsHookExW(WH_KEYBOARD_LL, _proc,      hMod, 0);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL,    _mouseProc, hMod, 0);
        if (_hook == 0)
        {
            // Bail cleanly: releasing here and throwing lets Main show the error and
            // exit. (Application.Exit() would be a no-op — the message loop isn't running yet.)
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            _tray.Visible = false;
            _tray.Dispose();
            throw new InvalidOperationException("Failed to install the keyboard hook.");
        }

        Log($"=== Started. hook={_hook}, snippets={_snippets.Count}, keys=[{string.Join(",", _snippets.Keys)}] ===");
        Balloon($"Running in the tray — {_snippets.Count} shortcuts active.\nType a shortcut in SSMS then press Tab.");
    }

    // ── Keyboard hook (hot path — keep this cheap) ─────────────────────────────
    nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int  vk       = Marshal.ReadInt32(lParam);
            int  flags    = Marshal.ReadInt32(lParam + 8);
            bool injected = (flags & LLKHF_INJECTED) != 0;

            // Modifier keydowns (Shift while typing an uppercase letter, CapsLock, …)
            // are not text — they must neither reset the buffer nor be added to it.
            if (!injected && !IsModifierKey(vk))
            {
                if (IsSsmsFocused())
                {
                    // Ctrl/Alt chords (Ctrl+S, Ctrl+Z, Alt+Tab, …) are commands, not
                    // typing: never expand on them, and drop whatever was buffered.
                    bool alt  = (flags & LLKHF_ALTDOWN) != 0;
                    bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                    // F12 = script object under caret to a new window; Ctrl+F12 = locate
                    // it in Object Explorer. Checked before the chord filter on purpose.
                    if (vk == VK_F12)
                    {
                        _buffer.Clear();
                        if (!alt && !_navBusy)
                        {
                            _navBusy = true;
                            bool toObjectExplorer = ctrl;
                            _ = Task.Run(() => GoToObjectAsync(toObjectExplorer));
                        }
                        return 1; // F12 is ours while SSMS is focused
                    }

                    if (alt || ctrl)
                    {
                        _buffer.Clear();
                    }
                    else if (vk == VK_TAB && _buffer.Length > 0 &&
                             (GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0) // Shift+Tab = unindent, not expand
                    {
                        var typed = _buffer.ToString();
                        _buffer.Clear();

                        bool match = _snippets.TryGetValue(typed, out var snippet);
                        Log($"TAB in SSMS. typed='{typed}', match={match}");
                        if (match)
                        {
                            int len = typed.Length;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(EXPAND_DELAY_MS);
                                if (!IsDisposed)
                                {
                                    try { Invoke(() => Expand(len, snippet!)); }
                                    catch { /* form closing */ }
                                }
                            });
                            return 1; // suppress the Tab
                        }
                    }
                    else if (vk == VK_BACK)
                    {
                        if (_buffer.Length > 0) _buffer.Length--;
                    }
                    else if (vk is (>= 0x41 and <= 0x5A) or (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x69)) // A–Z, 0–9, numpad 0–9
                    {
                        if (vk is >= 0x30 and <= 0x39 && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
                        {
                            _buffer.Clear(); // Shift+digit types a symbol (!, @, …), not the digit
                        }
                        else
                        {
                            // Reset if the user paused — stale characters from earlier
                            // typing must not glue onto a fresh shortcut.
                            long now = Environment.TickCount64;
                            if (now - _lastKeyTick > IDLE_RESET_MS) _buffer.Clear();
                            _lastKeyTick = now;

                            if (_buffer.Length < MAX_BUFFER)
                            {
                                _buffer.Append(vk switch
                                {
                                    <= 0x39 => (char)vk,                 // top-row digit
                                    >= 0x60 => (char)('0' + vk - 0x60),  // numpad digit
                                    _       => ToChar(vk),               // letter
                                });
                            }
                        }
                    }
                    else
                    {
                        _buffer.Clear(); // Tab w/o match, Enter, space, arrows, etc.
                    }
                }
                else if (_buffer.Length > 0)
                {
                    _buffer.Clear(); // focus is not SSMS — discard anything pending
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // A mouse click moves the caret, so anything typed before it is stale.
    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN))
        {
            if (_buffer.Length > 0) _buffer.Clear();
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // Shift/Ctrl/Alt (generic + left/right variants), CapsLock, Win keys.
    static bool IsModifierKey(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or 0x12 or VK_CAPITAL or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    static char ToChar(int vk)
    {
        bool shift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool caps  = (GetAsyncKeyState(VK_CAPITAL) & 0x0001) != 0;
        return (char)(vk + (shift ^ caps ? 0 : 32)); // lookups are case-insensitive anyway
    }

    /// <summary>
    /// Cheap on the hot path: GetForegroundWindow is a fast user32 call. We only
    /// resolve the owning process (expensive) when the foreground window changes.
    /// </summary>
    bool IsSsmsFocused()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == _cachedHwnd) return _cachedIsSsms;

        _cachedHwnd   = hwnd;
        _cachedIsSsms = false;
        if (hwnd != 0)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    _cachedIsSsms = p.ProcessName.StartsWith("Ssms", StringComparison.OrdinalIgnoreCase);
                }
                catch { /* process gone — leave false */ }
            }
        }
        return _cachedIsSsms;
    }

    // ── Expansion ──────────────────────────────────────────────────────────────
    void Expand(int deleteCount, Snippet snippet)
    {
        Log($"Expand: delete={deleteCount}, len={snippet.Text.Length}, left={snippet.LeftMoves}, sel={snippet.SelectLen}");
        string? prev = TryGetClipboardText();
        if (!TrySetClipboardText(snippet.Text))
        {
            Log("Expand: FAILED to set clipboard");
            return; // couldn't take the clipboard — abort, leave text intact
        }

        // Backspaces + Ctrl+V in a single SendInput call: the batch is inserted
        // into the input stream atomically, so a user keystroke can't land in the
        // middle of the sequence. (Paste handles newlines, brackets, any Unicode.)
        var inputs = new INPUT[deleteCount * 2 + 4];
        int n = 0;
        for (int i = 0; i < deleteCount; i++)
        {
            inputs[n++] = Key(VK_BACK);
            inputs[n++] = Key(VK_BACK, up: true);
        }
        inputs[n++] = Key(VK_CONTROL);
        inputs[n++] = Key(VK_V);
        inputs[n++] = Key(VK_V,       up: true);
        inputs[n++] = Key(VK_CONTROL, up: true);
        SendBatch(inputs);

        // After the paste lands, move the caret to the first field ($Literal$) and
        // select it, or to the $end$ marker. Deferred so SSMS finishes pasting first.
        if (snippet.LeftMoves > 0 || snippet.SelectLen > 0)
        {
            _ = Task.Delay(CARET_SETTLE_MS).ContinueWith(_ =>
            {
                if (!IsDisposed)
                {
                    try { Invoke(() => PositionCaret(snippet.LeftMoves, snippet.SelectLen)); } catch { }
                }
            });
        }

        if (prev != null)
        {
            _ = Task.Delay(CLIPBOARD_RESTORE_MS).ContinueWith(_ =>
            {
                if (!IsDisposed)
                {
                    try { Invoke(() => TrySetClipboardText(prev)); } catch { }
                }
            });
        }
    }

    // The caret sits at the end of the pasted text. Walk it left to the target,
    // then (if selecting a field) hold Shift and walk left to highlight it.
    // One atomic SendInput batch, so user keystrokes can't interleave.
    static void PositionCaret(int leftMoves, int selectLen)
    {
        var inputs = new INPUT[leftMoves * 2 + (selectLen > 0 ? selectLen * 2 + 2 : 0)];
        int n = 0;
        for (int i = 0; i < leftMoves; i++)
        {
            inputs[n++] = Key(VK_LEFT);
            inputs[n++] = Key(VK_LEFT, up: true);
        }
        if (selectLen > 0)
        {
            inputs[n++] = Key(VK_SHIFT);
            for (int i = 0; i < selectLen; i++)
            {
                inputs[n++] = Key(VK_LEFT);
                inputs[n++] = Key(VK_LEFT, up: true);
            }
            inputs[n++] = Key(VK_SHIFT, up: true);
        }
        SendBatch(inputs);
    }

    static INPUT Key(int vk, bool up = false) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    static void SendBatch(ReadOnlySpan<INPUT> inputs)
    {
        if (inputs.IsEmpty) return;
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) Log($"SendInput: only {sent}/{inputs.Length} events sent");
    }

    static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    static bool TrySetClipboardText(string text)
    {
        // Clipboard can be transiently locked by another app — retry briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(20); }
        }
        return false;
    }

    // ── Tray actions ─────────────────────────────────────────────────────────
    void OnReload(object? s, EventArgs e)
    {
        _snippets.Clear();
        foreach (var (k, v) in LoadSnippets()) _snippets[k] = v;
        _tray.Text     = TrayText();
        _menuInfo.Text = $"{_snippets.Count} snippets loaded";
        Balloon($"Reloaded {_snippets.Count} snippets");
    }

    void ShowStatus()
    {
        if (_snippets.Count == 0)
        {
            MessageBox.Show("No snippets found.\n\nExpected .snippet files under:\nDocuments\\SQL Server Management Studio*\\Snippets\\My Shortcuts",
                "SSMS Snippet Expander", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var list = string.Join("\n", _snippets.Keys.OrderBy(k => k).Select(k => $"  {k}"));
        MessageBox.Show($"Type the shortcut then press Tab.\n\nLoaded shortcuts:\n{list}",
            "SSMS Snippet Expander", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Balloon(string msg)
    {
        // Callable from any thread (F12 navigation runs on the thread pool).
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Balloon(msg)); } catch { }
            return;
        }
        _tray.BalloonTipTitle = "SSMS Snippets";
        _tray.BalloonTipText  = msg;
        _tray.ShowBalloonTip(2500);
    }

    string TrayText()
    {
        // NotifyIcon.Text is limited to 63 characters.
        var t = $"SSMS Snippets — {_snippets.Count} shortcuts active";
        return t.Length > 63 ? t[..63] : t;
    }

    // ── Snippet loader ─────────────────────────────────────────────────────────
    static Dictionary<string, Snippet> LoadSnippets()
    {
        var result = new Dictionary<string, Snippet>(StringComparer.OrdinalIgnoreCase);

        // 1. Built-in shortcuts embedded in the exe — so a standalone download works
        //    even before any .snippet files are copied to the SSMS folder.
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(".snippet", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                AddSnippet(result, XDocument.Load(stream));
            }
            catch { /* skip malformed resource */ }
        }

        // 2. User snippets on disk override the built-ins and add new ones.
        //    Documents can be OneDrive-redirected; SSMS uses the *physical* profile
        //    folder, so scan both the profile path and the redirected one.
        var baseDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var docs in baseDirs)
        {
            List<string> ssmsDirs;
            try
            {
                if (!Directory.Exists(docs)) continue;
                ssmsDirs = Directory.GetDirectories(docs, "SQL Server Management Studio*").ToList();
            }
            catch { continue; /* inaccessible base dir */ }

            foreach (var ssmsDir in ssmsDirs)
            {
                var dir = Path.Combine(ssmsDir, "Snippets", "My Shortcuts");
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*.snippet"))
                {
                    try { AddSnippet(result, XDocument.Load(file)); }
                    catch { /* skip malformed file */ }
                }
            }
        }
        return result;
    }

    /// <summary>Parses one snippet document and adds/overrides it in the map by shortcut.</summary>
    static void AddSnippet(Dictionary<string, Snippet> result, XDocument doc)
    {
        XNamespace ns = "http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet";

        var snippet = doc.Descendants(ns + "CodeSnippet").FirstOrDefault();
        if (snippet == null) return;

        var shortcut = snippet.Descendants(ns + "Shortcut").FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrEmpty(shortcut)) return;

        var literals = snippet.Descendants(ns + "Literal").ToDictionary(
            l => l.Element(ns + "ID")?.Value      ?? "",
            l => l.Element(ns + "Default")?.Value ?? "");

        var code = snippet.Descendants(ns + "Code").FirstOrDefault()?.Value;
        if (code == null) return;

        result[shortcut] = BuildSnippet(code, literals);
    }

    /// <summary>
    /// Substitutes $LiteralID$ with its default and removes $end$/$selected$, while
    /// recording where the caret should land. Newlines are normalised to '\n' so that
    /// each character equals exactly one caret stop (a line break is one Left press),
    /// which lets <see cref="PositionCaret"/> navigate purely by counting characters.
    /// Priority: select the first literal's default; else caret at $end$; else end.
    /// </summary>
    static Snippet BuildSnippet(string code, Dictionary<string, string> literals)
    {
        var raw = code.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        var sb = new StringBuilder(raw.Length);
        int firstLiteralStart = -1, firstLiteralLen = 0, endPos = -1;
        int last = 0;

        foreach (Match m in Regex.Matches(raw, @"\$(\w*)\$"))
        {
            sb.Append(raw, last, m.Index - last);
            last = m.Index + m.Length;

            var id = m.Groups[1].Value;
            if (id.Length == 0)
            {
                sb.Append('$'); // "$$" is the snippet-format escape for a literal $
            }
            else if (id == "end")
            {
                if (endPos < 0) endPos = sb.Length;
            }
            else if (id == "selected")
            {
                // surround-with target — unused for expansion snippets
            }
            else
            {
                var def = literals.TryGetValue(id, out var d) ? d : "";
                if (firstLiteralStart < 0 && def.Length > 0)
                {
                    firstLiteralStart = sb.Length;
                    firstLiteralLen   = def.Length;
                }
                sb.Append(def);
            }
        }
        sb.Append(raw, last, raw.Length - last);

        var text  = sb.ToString();
        int total = text.Length;

        int targetEnd; // caret-stop offset the caret should finish at
        int selectLen;
        if (firstLiteralStart >= 0)
        {
            targetEnd = firstLiteralStart + firstLiteralLen; // select the field
            selectLen = firstLiteralLen;
        }
        else if (endPos >= 0)
        {
            targetEnd = endPos;                              // caret at $end$
            selectLen = 0;
        }
        else
        {
            targetEnd = total;                               // natural end
            selectLen = 0;
        }

        return new Snippet(text, total - targetEnd, selectLen);
    }

    // ── Cleanup ────────────────────────────────────────────────────────────────
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_hook != 0)      UnhookWindowsHookEx(_hook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}

/// <summary>An expanded snippet plus where to leave the caret after pasting.</summary>
/// <param name="Text">Final text, newlines normalised to '\n' (1 char = 1 caret stop).</param>
/// <param name="LeftMoves">Left-arrow presses from the paste end to reach the target.</param>
/// <param name="SelectLen">If &gt; 0, Shift+Left presses to select the first field.</param>
sealed record Snippet(string Text, int LeftMoves, int SelectLen);
