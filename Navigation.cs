using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// F12 / Ctrl+F12 handling: figure out the identifier under the caret, resolve it
// against the database the active query window is connected to, then either
// script it into a new query window (F12) or select it in Object Explorer (Ctrl+F12).
partial class MainForm
{
    bool _navBusy; // one navigation at a time (also swallows F12 auto-repeat)

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hWnd, Span<char> buffer, int nMaxCount);

    static string GetWindowTitle(nint hwnd)
    {
        Span<char> buf = stackalloc char[512];
        int len = GetWindowTextW(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf[..len]) : "";
    }

    /// <summary>
    /// The SSMS main-window title embeds the active window's connection:
    /// "SQLQuery1.sql - SERVER\INST.MyDb (DOMAIN\user (57))* - Microsoft SQL Server Management Studio".
    /// We split "SERVER\INST.MyDb" on its LAST dot (db names with dots will mis-parse — rare).
    /// </summary>
    internal static (string Server, string Database)? ParseConnectionFromTitle(string title)
    {
        var m = Regex.Match(title, @"(?<sd>\S+)\s\([^()]*\(\d+\)\)");
        if (!m.Success) return null;

        var sd  = m.Groups["sd"].Value;
        int dot = sd.LastIndexOf('.');
        if (dot <= 0 || dot == sd.Length - 1) return null;
        return (sd[..dot], sd[(dot + 1)..]);
    }

    // ── UI-thread marshalling helpers (clipboard must run on the STA thread) ──
    void UiInvoke(Action action)
    {
        try { if (!IsDisposed) Invoke(action); } catch { }
    }

    T? UiInvoke<T>(Func<T?> func)
    {
        try { return IsDisposed ? default : Invoke(func); } catch { return default; }
    }

    // ── Main flow ─────────────────────────────────────────────────────────────
    async Task GoToObjectAsync(bool toObjectExplorer)
    {
        try
        {
            nint   ssmsWnd = GetForegroundWindow();
            string title   = GetWindowTitle(ssmsWnd);
            var    conn    = ParseConnectionFromTitle(title);
            if (conn is null)
            {
                Balloon("Couldn't read the server/database from the SSMS window title.\nIs the query window connected?");
                return;
            }
            var (server, db) = conn.Value;

            string? prevClip = UiInvoke(TryGetClipboardText);
            string? word     = await CaptureWordAtCaretAsync();
            if (string.IsNullOrEmpty(word))
            {
                RestoreClipboard(prevClip);
                Balloon("No identifier under the caret.");
                return;
            }
            Log($"F12: word='{word}', server='{server}', db='{db}', oe={toObjectExplorer}");

            var obj = await Task.Run(() => SqlObjectService.Resolve(server, db, word));
            if (obj is null)
            {
                RestoreClipboard(prevClip);
                Balloon($"'{word}' — no table, view, procedure or function with that name in {db}.");
                return;
            }

            if (toObjectExplorer)
            {
                RestoreClipboard(prevClip);
                // F8 opens/focuses Object Explorer so the tree exists and is visible.
                SendBatch([Key(VK_CONTROL, up: true), Key(VK_F8), Key(VK_F8, up: true)]);
                await Task.Delay(400);
                string? error = await Task.Run(() => ObjectExplorerNavigator.TryLocate(ssmsWnd, server, db, obj, Log));
                if (error != null) Balloon("Ctrl+F12: " + error);
            }
            else
            {
                string script = await Task.Run(() => SqlObjectService.Script(server, db, obj));
                await PasteInNewWindowAsync(ssmsWnd, script);
                if (prevClip != null)
                {
                    await Task.Delay(CLIPBOARD_RESTORE_MS);
                    RestoreClipboard(prevClip);
                }
            }
        }
        catch (Exception ex)
        {
            Log("F12 failed: " + ex);
            Balloon("F12 failed: " + ex.Message);
        }
        finally
        {
            _navBusy = false;
        }
    }

    void RestoreClipboard(string? prev)
    {
        if (prev != null) UiInvoke(() => TrySetClipboardText(prev));
    }

    /// <summary>
    /// Grabs the word under the caret without an editor API: Ctrl+Right jumps past
    /// the current word, Ctrl+Shift+Left selects back to its start, Ctrl+C copies it,
    /// and a final Right collapses the selection so later typing can't overwrite it.
    /// The leading Ctrl-up neutralises the physically held Ctrl from Ctrl+F12.
    /// </summary>
    async Task<string?> CaptureWordAtCaretAsync()
    {
        UiInvoke(() => { try { Clipboard.Clear(); } catch { } });

        SendBatch([
            Key(VK_CONTROL, up: true),
            Key(VK_CONTROL), Key(VK_RIGHT), Key(VK_RIGHT, up: true), Key(VK_CONTROL, up: true),
            Key(VK_CONTROL), Key(VK_SHIFT), Key(VK_LEFT), Key(VK_LEFT, up: true), Key(VK_SHIFT, up: true), Key(VK_CONTROL, up: true),
            Key(VK_CONTROL), Key(VK_C), Key(VK_C, up: true), Key(VK_CONTROL, up: true),
            Key(VK_RIGHT), Key(VK_RIGHT, up: true),
        ]);

        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(80);
            string? text = UiInvoke(TryGetClipboardText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Strip brackets/punctuation/whitespace — keep the first identifier.
                var m = Regex.Match(text, @"[A-Za-z_@#][A-Za-z0-9_@#$]*");
                return m.Success ? m.Value : null;
            }
        }
        return null;
    }

    /// <summary>Puts the script on the clipboard, opens a new query window (Ctrl+N
    /// inherits the current connection), waits for it by watching the window title,
    /// then pastes.</summary>
    async Task PasteInNewWindowAsync(nint ssmsWnd, string script)
    {
        bool ok = UiInvoke<bool?>(() => TrySetClipboardText(script)) ?? false;
        if (!ok)
        {
            Balloon("Couldn't put the script on the clipboard.");
            return;
        }

        string before = GetWindowTitle(ssmsWnd);
        SendBatch([Key(VK_CONTROL, up: true), Key(VK_CONTROL), Key(VK_N), Key(VK_N, up: true), Key(VK_CONTROL, up: true)]);

        for (int i = 0; i < 40; i++) // up to 4 s for the new tab to appear
        {
            await Task.Delay(100);
            if (GetWindowTitle(GetForegroundWindow()) != before) break;
        }
        await Task.Delay(250); // let the new editor finish initialising

        SendBatch([Key(VK_CONTROL), Key(VK_V), Key(VK_V, up: true), Key(VK_CONTROL, up: true)]);
    }
}
