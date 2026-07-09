using System.Windows.Automation;

/// <summary>
/// Ctrl+F12 (tray-app version): drives the Object Explorer tree from outside SSMS
/// via UI Automation — find the tree, walk Server → Databases → db → type folder,
/// expand lazily-loaded nodes with retries, then select the target.
/// Inherently best-effort; the in-process VSIX extension does this natively.
/// </summary>
internal static class ObjectExplorerNavigator
{
    public static string? TryLocate(nint mainWindow, string server, string db, DbObject obj, Action<string> log)
    {
        string[] folders = obj.Type switch
        {
            "U"          => new[] { "Tables" },
            "V"          => new[] { "Views" },
            "P"          => new[] { "Programmability", "Stored Procedures" },
            "FN"         => new[] { "Programmability", "Functions", "Scalar-valued Functions" },
            "IF" or "TF" => new[] { "Programmability", "Functions", "Table-valued Functions" },
            _            => Array.Empty<string>(),
        };
        if (folders.Length == 0)
            return $"Object type '{obj.Type}' isn't supported for Object Explorer navigation.";

        try
        {
            var root = AutomationElement.FromHandle(mainWindow);
            var tree = FindTree(root);
            if (tree == null) return "Couldn't find the Object Explorer tree (is the panel open?).";

            // Server node text looks like "SERVER\INST (SQL Server 16.0.x - DOMAIN\user)".
            var node = FindChild(tree, n => n.StartsWith(server, StringComparison.OrdinalIgnoreCase), 4000);
            if (node == null)
            {
                var all = Children(tree);
                if (all.Count == 1) node = all[0]; // single connection — use it
            }
            if (node == null) return $"No Object Explorer connection matching '{server}'.";
            log($"OE: server node '{Name(node)}'");

            foreach (var step in new[] { "Databases", db }.Concat(folders))
            {
                Expand(node);
                var next = FindChild(node,
                    n => n.Equals(step, StringComparison.OrdinalIgnoreCase)
                      || n.StartsWith(step + " ", StringComparison.OrdinalIgnoreCase), 6000);
                if (next == null) return $"Couldn't find '{step}' under '{Name(node)}'.";
                node = next;
            }

            Expand(node);
            string target = $"{obj.Schema}.{obj.Name}";
            var item = FindChild(node,
                n => n.Equals(target, StringComparison.OrdinalIgnoreCase)
                  || n.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase), 8000);
            if (item == null) return $"Couldn't find '{target}' in Object Explorer.";

            try { if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var sp)) ((ScrollItemPattern)sp).ScrollIntoView(); } catch { }
            try { if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) ((SelectionItemPattern)sel).Select(); } catch { }
            try { item.SetFocus(); } catch { }
            return null; // success
        }
        catch (Exception ex)
        {
            log("OE navigation failed: " + ex);
            return "Object Explorer navigation failed: " + ex.Message;
        }
    }

    static AutomationElement? FindTree(AutomationElement root)
    {
        var pane = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Object Explorer"));
        var scope = pane ?? root;
        return scope.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree));
    }

    static string Name(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; } catch { return ""; }
    }

    static List<AutomationElement> Children(AutomationElement parent)
    {
        var result = new List<AutomationElement>();
        try
        {
            foreach (AutomationElement child in parent.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
            {
                result.Add(child);
            }
        }
        catch { }
        return result;
    }

    /// <summary>Polls for a child tree item — expanded nodes populate asynchronously
    /// ("Expanding..." placeholder first), so retry until the timeout.</summary>
    static AutomationElement? FindChild(AutomationElement parent, Func<string, bool> match, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            foreach (var child in Children(parent))
            {
                if (match(Name(child))) return child;
            }
            Thread.Sleep(200);
        } while (Environment.TickCount64 < deadline);
        return null;
    }

    static void Expand(AutomationElement item)
    {
        try
        {
            if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var p))
            {
                var ec = (ExpandCollapsePattern)p;
                if (ec.Current.ExpandCollapseState != ExpandCollapseState.Expanded) ec.Expand();
            }
        }
        catch { }
    }
}
