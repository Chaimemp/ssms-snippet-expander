using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace SsmsSnippetExpander.Extension
{
    /// <summary>
    /// SSMS 22 in-process package. F12 scripts the object under the caret to a new
    /// query window; Ctrl+F12 selects it in Object Explorer. Patterned on the
    /// open-source ssms-object-explorer-menu extension (tested on SSMS 22.x).
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class SnippetExpanderPackage : AsyncPackage
    {
        public const string PackageGuidString = "7A1E4C9D-3B52-4F8E-9C47-D2A8F0B6E5A1";
        public static readonly Guid CommandSet = new Guid("2F6B9E14-8D07-4C53-A9B2-51E3C7D0F8A6");
        public const int CmdIdScriptObject = 0x0100;
        public const int CmdIdLocateInOE   = 0x0101;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var mcs = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            if (mcs == null) return;

            mcs.AddCommand(new MenuCommand(OnScriptObject, new CommandID(CommandSet, CmdIdScriptObject)));
            mcs.AddCommand(new MenuCommand(OnLocateInOE,   new CommandID(CommandSet, CmdIdLocateInOE)));
        }

        void OnScriptObject(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                new GoToDefinitionService(this).ScriptObjectUnderCaret();
            }
            catch (Exception ex)
            {
                ShowError("Script Object failed", ex);
            }
        }

        void OnLocateInOE(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                new GoToDefinitionService(this).LocateUnderCaretInObjectExplorer();
            }
            catch (Exception ex)
            {
                ShowError("Locate in Object Explorer failed", ex);
            }
        }

        internal static void ShowError(string title, Exception ex)
        {
            MessageBox.Show(ex.Message, "SSMS Snippet Expander — " + title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        internal static void ShowInfo(string message)
        {
            MessageBox.Show(message, "SSMS Snippet Expander",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
