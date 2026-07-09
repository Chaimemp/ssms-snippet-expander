using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SsmsSnippetExpander.Extension
{
    /// <summary>The expansion text plus where to leave the caret (offsets into Text).</summary>
    internal sealed class ExpandedSnippet
    {
        public string Text;
        public int CaretOffset;   // caret lands here after insertion
        public int SelectLength;  // if > 0, select this many chars ending at CaretOffset
    }

    /// <summary>
    /// Loads .snippet files from Documents\SQL Server Management Studio*\Snippets\My Shortcuts
    /// (both the physical profile folder and a OneDrive-redirected Documents), keyed by
    /// &lt;Shortcut&gt;. Same format/behaviour as the tray app's loader.
    /// </summary>
    internal static class SnippetLibrary
    {
        static Dictionary<string, ExpandedSnippet> _snippets;
        static readonly object Sync = new object();

        public static bool TryGet(string shortcut, out ExpandedSnippet snippet)
        {
            lock (Sync)
            {
                if (_snippets == null) _snippets = Load();
                return _snippets.TryGetValue(shortcut, out snippet);
            }
        }

        public static int Reload()
        {
            lock (Sync)
            {
                _snippets = Load();
                return _snippets.Count;
            }
        }

        static Dictionary<string, ExpandedSnippet> Load()
        {
            var result = new Dictionary<string, ExpandedSnippet>(StringComparer.OrdinalIgnoreCase);

            var baseDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string docs in baseDirs)
            {
                string[] ssmsDirs;
                try
                {
                    if (!Directory.Exists(docs)) continue;
                    ssmsDirs = Directory.GetDirectories(docs, "SQL Server Management Studio*");
                }
                catch { continue; }

                foreach (string ssmsDir in ssmsDirs)
                {
                    string dir = Path.Combine(ssmsDir, "Snippets", "My Shortcuts");
                    if (!Directory.Exists(dir)) continue;

                    foreach (string file in Directory.GetFiles(dir, "*.snippet"))
                    {
                        try { AddSnippet(result, XDocument.Load(file)); }
                        catch { /* skip malformed file */ }
                    }
                }
            }
            return result;
        }

        static void AddSnippet(Dictionary<string, ExpandedSnippet> result, XDocument doc)
        {
            XNamespace ns = "http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet";

            XElement snippet = doc.Descendants(ns + "CodeSnippet").FirstOrDefault();
            if (snippet == null) return;

            XElement shortcutElement = snippet.Descendants(ns + "Shortcut").FirstOrDefault();
            string shortcut = shortcutElement == null ? null : (shortcutElement.Value ?? "").Trim();
            if (string.IsNullOrEmpty(shortcut)) return;

            var literals = new Dictionary<string, string>();
            foreach (XElement l in snippet.Descendants(ns + "Literal"))
            {
                XElement id = l.Element(ns + "ID");
                XElement def = l.Element(ns + "Default");
                literals[id == null ? "" : id.Value] = def == null ? "" : def.Value;
            }

            XElement code = snippet.Descendants(ns + "Code").FirstOrDefault();
            if (code == null) return;

            result[shortcut] = Build(code.Value, literals);
        }

        /// <summary>Substitutes $LiteralID$ defaults, honours $end$, "$$" escapes a
        /// literal dollar. Newlines normalised to \r\n (the editor's convention).</summary>
        static ExpandedSnippet Build(string code, Dictionary<string, string> literals)
        {
            string raw = code.Replace("\r\n", "\n").Replace("\r", "\n").Trim().Replace("\n", "\r\n");

            var sb = new StringBuilder(raw.Length);
            int firstLiteralStart = -1, firstLiteralLen = 0, endPos = -1;
            int last = 0;

            foreach (Match m in Regex.Matches(raw, @"\$(\w*)\$"))
            {
                sb.Append(raw, last, m.Index - last);
                last = m.Index + m.Length;

                string id = m.Groups[1].Value;
                if (id.Length == 0)
                {
                    sb.Append('$');
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
                    string def;
                    if (!literals.TryGetValue(id, out def)) def = "";
                    if (firstLiteralStart < 0 && def.Length > 0)
                    {
                        firstLiteralStart = sb.Length;
                        firstLiteralLen   = def.Length;
                    }
                    sb.Append(def);
                }
            }
            sb.Append(raw, last, raw.Length - last);

            string text = sb.ToString();
            int caretOffset;
            int selectLength;
            if (firstLiteralStart >= 0)
            {
                caretOffset  = firstLiteralStart + firstLiteralLen;
                selectLength = firstLiteralLen;
            }
            else if (endPos >= 0)
            {
                caretOffset  = endPos;
                selectLength = 0;
            }
            else
            {
                caretOffset  = text.Length;
                selectLength = 0;
            }

            return new ExpandedSnippet { Text = text, CaretOffset = caretOffset, SelectLength = selectLength };
        }
    }
}
