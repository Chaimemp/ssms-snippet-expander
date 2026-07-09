using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using System;
using System.Reflection;
using System.Windows.Forms;

namespace SsmsSnippetExpander.Extension
{
    /// <summary>
    /// Selects an object in the Object Explorer tree. Being in-process, we can grab
    /// the actual WinForms TreeView (via IObjectExplorerService's non-public Tree
    /// property — the same technique ssms-object-explorer-menu uses on SSMS 22).
    /// Returns null on success or a human-readable error.
    /// </summary>
    internal static class ObjectExplorerLocator
    {
        public static string TryLocate(AsyncPackage package, string serverName, string database, GoToDefinitionService.ResolvedObject obj)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string[] folders;
            switch (obj.Type)
            {
                case "U":  folders = new[] { "Tables" }; break;
                case "V":  folders = new[] { "Views" }; break;
                case "P":  folders = new[] { "Programmability", "Stored Procedures" }; break;
                case "FN": folders = new[] { "Programmability", "Functions", "Scalar-valued Functions" }; break;
                case "IF":
                case "TF": folders = new[] { "Programmability", "Functions", "Table-valued Functions" }; break;
                default:   return "Object type '" + obj.Type + "' is not supported.";
            }

            var oeService = (IObjectExplorerService)((IServiceProvider)package).GetService(typeof(IObjectExplorerService));
            if (oeService == null) return "Object Explorer service not found.";

            PropertyInfo treeProperty = oeService.GetType().GetProperty(
                "Tree", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (treeProperty == null) return "Object Explorer tree not accessible in this SSMS build.";

            var tree = (TreeView)treeProperty.GetValue(oeService, null);
            if (tree == null || tree.Nodes.Count == 0) return "Object Explorer has no connections.";

            // Server node text: "SERVER\INST (SQL Server 16.0.x - DOMAIN\user)"
            TreeNode node = FindNode(tree.Nodes, serverName);
            if (node == null && tree.Nodes.Count == 1) node = tree.Nodes[0];
            if (node == null) return "No Object Explorer connection matching '" + serverName + "'.";

            string[] path = BuildPath(database, folders);
            foreach (string step in path)
            {
                node.Expand();
                TreeNode next = WaitForChild(node, step);
                if (next == null) return "Couldn't find '" + step + "' under '" + node.Text + "'.";
                node = next;
            }

            node.Expand();
            string target = obj.Schema + "." + obj.Name;
            TreeNode item = WaitForChild(node, target);
            if (item == null) return "Couldn't find '" + target + "' in Object Explorer.";

            tree.SelectedNode = item;
            item.EnsureVisible();
            tree.Focus();
            return null;
        }

        static string[] BuildPath(string database, string[] folders)
        {
            var path = new string[folders.Length + 2];
            path[0] = "Databases";
            path[1] = database;
            Array.Copy(folders, 0, path, 2, folders.Length);
            return path;
        }

        static TreeNode FindNode(TreeNodeCollection nodes, string text)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Text.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                    n.Text.StartsWith(text + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return n;
                }
            }
            return null;
        }

        /// <summary>Object Explorer populates children lazily ("Expanding..." placeholder),
        /// so pump messages briefly while waiting for the real nodes to arrive.</summary>
        static TreeNode WaitForChild(TreeNode parent, string text)
        {
            for (int attempt = 0; attempt < 50; attempt++) // ~5 s
            {
                TreeNode found = FindNode(parent.Nodes, text);
                if (found != null) return found;

                Application.DoEvents();
                System.Threading.Thread.Sleep(100);
            }
            return FindNode(parent.Nodes, text);
        }
    }
}
