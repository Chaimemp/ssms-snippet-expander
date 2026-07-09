using EnvDTE;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Specialized;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsSnippetExpander.Extension
{
    /// <summary>
    /// The in-process version of F12/Ctrl+F12: real editor APIs for the word under
    /// the caret, the query window's actual connection (ServiceCache), SMO for
    /// scripting, and CreateNewBlankScript for the new window.
    /// </summary>
    internal sealed class GoToDefinitionService
    {
        readonly AsyncPackage _package;

        public GoToDefinitionService(AsyncPackage package)
        {
            _package = package;
        }

        // ── F12 ────────────────────────────────────────────────────────────────
        public void ScriptObjectUnderCaret()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string word = GetWordUnderCaret();
            if (string.IsNullOrEmpty(word))
            {
                SnippetExpanderPackage.ShowInfo("No identifier under the caret.");
                return;
            }

            UIConnectionInfo ci = CurrentConnection();
            if (ci == null)
            {
                SnippetExpanderPackage.ShowInfo("The active query window has no connection.");
                return;
            }

            Database db = ConnectSmo(ci);
            ResolvedObject obj = ResolveObject(db, word);
            if (obj == null)
            {
                SnippetExpanderPackage.ShowInfo(
                    "'" + word + "' — no table, view, procedure or function with that name in " + db.Name + ".");
                return;
            }

            string script = ScriptCreate(db, obj);

            // New query window on the same connection, then insert the script.
            ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, ci, null);
            DTE dte = (DTE)Package.GetGlobalService(typeof(DTE));
            if (dte != null && dte.ActiveDocument != null)
            {
                TextSelection sel = (TextSelection)dte.ActiveDocument.Selection;
                sel.Insert(script, (int)vsInsertFlags.vsInsertFlagsInsertAtStart);
                sel.StartOfDocument(false);
            }
        }

        // ── Ctrl+F12 ───────────────────────────────────────────────────────────
        public void LocateUnderCaretInObjectExplorer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string word = GetWordUnderCaret();
            if (string.IsNullOrEmpty(word))
            {
                SnippetExpanderPackage.ShowInfo("No identifier under the caret.");
                return;
            }

            UIConnectionInfo ci = CurrentConnection();
            if (ci == null)
            {
                SnippetExpanderPackage.ShowInfo("The active query window has no connection.");
                return;
            }

            Database db = ConnectSmo(ci);
            ResolvedObject obj = ResolveObject(db, word);
            if (obj == null)
            {
                SnippetExpanderPackage.ShowInfo(
                    "'" + word + "' — no table, view, procedure or function with that name in " + db.Name + ".");
                return;
            }

            string error = ObjectExplorerLocator.TryLocate(_package, ci.ServerName, db.Name, obj);
            if (error != null)
            {
                SnippetExpanderPackage.ShowInfo("Ctrl+F12: " + error);
            }
        }

        // ── Editor: word under caret via real text APIs (no clipboard tricks) ──
        string GetWordUnderCaret()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = (DTE)Package.GetGlobalService(typeof(DTE));
            if (dte == null || dte.ActiveDocument == null) return null;

            TextSelection sel = (TextSelection)dte.ActiveDocument.Selection;
            if (!string.IsNullOrEmpty(sel.Text))
            {
                Match selMatch = Regex.Match(sel.Text, @"[A-Za-z_@#][A-Za-z0-9_@#$]*");
                return selMatch.Success ? selMatch.Value : null;
            }

            // Deterministic word-at-caret: scan the whole line and pick the identifier
            // whose span contains (or ends exactly at) the caret column. Avoids the
            // WordLeft/WordRight edge case where a caret at the START of a word would
            // resolve to the previous word.
            EditPoint caret     = sel.ActivePoint.CreateEditPoint();
            EditPoint lineStart = sel.ActivePoint.CreateEditPoint();
            lineStart.StartOfLine();
            EditPoint lineEnd   = sel.ActivePoint.CreateEditPoint();
            lineEnd.EndOfLine();

            string line = lineStart.GetText(lineEnd) ?? "";
            int column  = caret.LineCharOffset - 1; // LineCharOffset is 1-based

            foreach (Match m in Regex.Matches(line, @"[A-Za-z_@#][A-Za-z0-9_@#$]*"))
            {
                if (column >= m.Index && column <= m.Index + m.Length) return m.Value;
            }
            return null;
        }

        // ── Connection of the ACTIVE window — the big win over the tray app ────
        static UIConnectionInfo CurrentConnection()
        {
            IScriptFactory factory = ServiceCache.ScriptFactory;
            if (factory == null || factory.CurrentlyActiveWndConnectionInfo == null) return null;
            return factory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo;
        }

        static Database ConnectSmo(UIConnectionInfo ci)
        {
            ServerConnection conn = new ServerConnection(ci.ServerName);
            if (!string.IsNullOrEmpty(ci.UserName) && !string.IsNullOrEmpty(ci.Password))
            {
                conn.LoginSecure = false;
                conn.Login       = ci.UserName;
                conn.Password    = ci.Password;
            }
            else
            {
                conn.LoginSecure = true;
            }

            Server server = new Server(conn);

            string dbName = ci.AdvancedOptions["DATABASE"];
            if (string.IsNullOrEmpty(dbName)) dbName = "master";
            Database db = server.Databases[dbName];
            if (db == null) throw new InvalidOperationException("Database '" + dbName + "' not found on " + ci.ServerName + ".");
            return db;
        }

        // ── Resolution + scripting via SMO ─────────────────────────────────────
        internal sealed class ResolvedObject
        {
            public string Schema;
            public string Name;
            public string Type; // U, V, P, FN, IF, TF
        }

        static ResolvedObject ResolveObject(Database db, string name)
        {
            DataSet ds = db.ExecuteWithResults(
                "SELECT TOP (1) s.name, RTRIM(o.[type]), o.name " +
                "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                "WHERE o.name = N'" + name.Replace("'", "''") + "' " +
                "AND o.[type] IN ('U','V','P','FN','IF','TF') " +
                "ORDER BY CASE WHEN s.name = 'dbo' THEN 0 ELSE 1 END, s.name;");

            if (ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0) return null;
            DataRow row = ds.Tables[0].Rows[0];
            return new ResolvedObject
            {
                Schema = (string)row[0],
                Type   = (string)row[1],
                Name   = (string)row[2],
            };
        }

        static string ScriptCreate(Database db, ResolvedObject obj)
        {
            ScriptingOptions options = new ScriptingOptions
            {
                SchemaQualify          = true,
                DriAll                 = true,   // constraints + FKs for tables
                Indexes                = true,
                ScriptBatchTerminator  = true,
                EnforceScriptingOptions = true,
            };

            SqlSmoObject smo;
            switch (obj.Type)
            {
                case "U": smo = db.Tables[obj.Name, obj.Schema]; break;
                case "V": smo = db.Views[obj.Name, obj.Schema]; break;
                case "P": smo = db.StoredProcedures[obj.Name, obj.Schema]; break;
                default:  smo = db.UserDefinedFunctions[obj.Name, obj.Schema]; break;
            }
            if (smo == null)
                throw new InvalidOperationException("SMO could not load " + obj.Schema + "." + obj.Name + ".");

            StringCollection lines = ((IScriptable)smo).Script(options);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("-- " + obj.Schema + "." + obj.Name + "  (scripted from " + db.Parent.Name + "." + db.Name + ")");
            sb.AppendLine("USE [" + db.Name + "];");
            sb.AppendLine("GO");
            sb.AppendLine();
            foreach (string line in lines)
            {
                sb.AppendLine(line);
                sb.AppendLine("GO");
            }
            return sb.ToString();
        }
    }
}
