using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;

namespace SsmsSnippetExpander.Extension
{
    /// <summary>
    /// Hooks every editable text view and adds a command filter — the same technique
    /// SQL Prompt uses for shortcut-then-Tab expansion, but in-process with real
    /// buffer edits (no clipboard, no synthetic keystrokes).
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class TabExpansionViewListener : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            IWpfTextView view = AdapterService.GetWpfTextView(textViewAdapter);
            if (view == null) return;

            var filter = new TabExpansionFilter(view);
            IOleCommandTarget next;
            if (textViewAdapter.AddCommandFilter(filter, out next) == VSConstants.S_OK)
            {
                filter.Next = next;
            }
        }
    }

    internal sealed class TabExpansionFilter : IOleCommandTarget
    {
        static readonly Regex WordBeforeCaret = new Regex(@"[A-Za-z0-9_]+$", RegexOptions.Compiled);

        readonly IWpfTextView _view;
        internal IOleCommandTarget Next;

        public TabExpansionFilter(IWpfTextView view)
        {
            _view = view;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K
                && nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB
                && _view.Selection.IsEmpty
                && TryExpand())
            {
                return VSConstants.S_OK; // swallow the Tab
            }

            return Next != null
                ? Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
                : VSConstants.S_OK;
        }

        bool TryExpand()
        {
            try
            {
                SnapshotPoint caret = _view.Caret.Position.BufferPosition;
                ITextSnapshotLine line = caret.GetContainingLine();
                string beforeCaret = line.GetText().Substring(0, caret.Position - line.Start.Position);

                Match m = WordBeforeCaret.Match(beforeCaret);
                if (!m.Success) return false;

                ExpandedSnippet snippet;
                if (!SnippetLibrary.TryGet(m.Value, out snippet)) return false;

                int start = line.Start.Position + m.Index;
                ITextBuffer buffer = _view.TextBuffer;
                using (ITextEdit edit = buffer.CreateEdit())
                {
                    edit.Replace(new Span(start, m.Length), snippet.Text);
                    edit.Apply();
                }

                // Place the caret at the target (selecting the first field if any).
                ITextSnapshot snapshot = buffer.CurrentSnapshot;
                int caretPos = Math.Min(start + snippet.CaretOffset, snapshot.Length);
                if (snippet.SelectLength > 0 && caretPos - snippet.SelectLength >= 0)
                {
                    _view.Selection.Select(
                        new SnapshotSpan(snapshot, caretPos - snippet.SelectLength, snippet.SelectLength), false);
                }
                _view.Caret.MoveTo(new SnapshotPoint(snapshot, caretPos));
                _view.Caret.EnsureVisible();
                return true;
            }
            catch
            {
                return false; // never break typing — fall through to the normal Tab
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return Next != null
                ? Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
                : VSConstants.S_OK;
        }
    }
}
