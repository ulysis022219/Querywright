using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>
    /// Sees F12 and SSMS's Query.Execute before it runs and asks first when the SQL about to run holds a DELETE or UPDATE without
    /// WHERE. Never executes anything itself: "Execute anyway" just lets SSMS's own command continue.
    /// </summary>
    internal sealed class PriorityCommands : IOleCommandTarget
    {
        private readonly WorkbenchPackage package;
        private Guid group;
        private uint id;

        private PriorityCommands(WorkbenchPackage package) => this.package = package;

        internal static void Start(WorkbenchPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Query.Execute's GUID/ID are SSMS internals; read them from the command table instead of hard-coding.
                var dte = Package.GetGlobalService(typeof(SDTE));
                var commandList = dte.GetType().InvokeMember("Commands", System.Reflection.BindingFlags.GetProperty, null, dte, null);
                var command = commandList.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, commandList, new object[] { "Query.Execute", -1 });
                var guard = new PriorityCommands(package)
                {
                    group = new Guid((string)command.GetType().InvokeMember("Guid", System.Reflection.BindingFlags.GetProperty, null, command, null)),
                    id = (uint)(int)command.GetType().InvokeMember("ID", System.Reflection.BindingFlags.GetProperty, null, command, null),
                };
                var register = (IVsRegisterPriorityCommandTarget)Package.GetGlobalService(typeof(SVsRegisterPriorityCommandTarget));
                ErrorHandler.ThrowOnFailure(register.RegisterPriorityCommandTarget(0, guard, out _));
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "Execute warning unavailable: " + error.GetType().Name);
            }
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Keep F12 enabled in SQL editors even when SSMS's language service disables Go To Definition.
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && cCmds == 1 && prgCmds[0].cmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn)
            {
                try
                {
                    package.GetSqlView();
                    prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                    return VSConstants.S_OK;
                }
                catch (InvalidOperationException) { }
            }
            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // NOTSUPPORTED passes the command on to SSMS; S_OK swallows it.
            // F12 comes here too: SSMS's language service claims GotoDefn before editor filters see it once connected.
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && nCmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn)
            {
                SelfTest.Note = "f12 priority";
                try { return package.TryGoToDefinition(package.GetSqlView()) ? VSConstants.S_OK : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED; }
                catch (InvalidOperationException) { SelfTest.Note = "f12 no view"; return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED; }
            }
            const int pass = (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            if (pguidCmdGroup != group || nCmdID != id) return pass;
            try
            {
                var view = package.GetSqlView();
                if (package.Options?.WarnUnfilteredChanges == true)
                {
                    // SSMS runs the selection when there is one, otherwise the whole window.
                    string sql = view.Selection.IsEmpty ? view.TextSnapshot.GetText()
                        : string.Join("\n", view.Selection.SelectedSpans.Select(s => s.GetText()));
                    var targets = sql.Length > 1_000_000 ? System.Array.Empty<string>() : SqlAnalysis.UnfilteredChanges(sql);
                    if (targets.Count > 0 && !Ask(targets)) return VSConstants.S_OK;
                }
                // Tab history keeps an executed version, like SQL Prompt's ▶ entries.
                if (view.Properties.TryGetProperty("QuerywrightHistory", out Action<bool> save)) save(true);
            }
            catch (InvalidOperationException) { }
            return pass;
        }

        private bool Ask(System.Collections.Generic.IReadOnlyList<string> targets)
        {
            using (var form = new Form
            {
                Text = "Querywright: execution warning", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            })
            {
                var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0),
                    Text = "You're about to execute " + (targets.Count == 1 ? "a statement" : targets.Count + " statements") + " without a WHERE clause:" });
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0), Font = new System.Drawing.Font(form.Font, System.Drawing.FontStyle.Bold),
                    Text = string.Join(Environment.NewLine, targets.Take(10)) + (targets.Count > 10 ? Environment.NewLine + "..." : ""), Margin = new Padding(3, 8, 3, 12) });
                var never = new CheckBox { AutoSize = true, Text = "Don't show this warning again" };
                layout.Controls.Add(never);
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
                var cancel = new Button { Text = "Don't execute", DialogResult = DialogResult.Cancel, AutoSize = true };
                var run = new Button { Text = "Execute anyway", DialogResult = DialogResult.OK, AutoSize = true };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(run);
                layout.Controls.Add(buttons);
                form.Controls.Add(layout);
                form.AcceptButton = cancel; // Enter does not run it
                form.CancelButton = cancel;
                form.Shown += (sender, args) => cancel.Focus();
                bool execute = form.ShowDialog() == DialogResult.OK;
                if (never.Checked)
                {
                    package.Options.WarnUnfilteredChanges = false;
                    package.Options.SaveSettingsToStorage();
                }
                return execute;
            }
        }
    }
}
