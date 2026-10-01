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
            var guard = new PriorityCommands(package);
            object commandList = null;
            try
            {
                // Query.Execute's GUID/ID are SSMS internals; read them from the command table instead of hard-coding.
                var dte = Package.GetGlobalService(typeof(SDTE));
                commandList = Get(dte, "Commands");
                var command = commandList.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, commandList, new object[] { "Query.Execute", -1 });
                guard.group = new Guid((string)Get(command, "Guid"));
                guard.id = (uint)(int)Get(command, "ID");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "Execute warning unavailable: " + error.GetType().Name);
            }
            try
            {
                // Registered even without Query.Execute, so F12 still reaches us.
                var register = (IVsRegisterPriorityCommandTarget)Package.GetGlobalService(typeof(SVsRegisterPriorityCommandTarget));
                ErrorHandler.ThrowOnFailure(register.RegisterPriorityCommandTarget(0, guard, out _));
                if (commandList != null) _ = package.JoinableTaskFactory.StartOnIdle(() => FindF12(commandList));
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "Priority commands unavailable: " + error.GetType().Name);
            }
        }

        /// <summary>Definition commands the keyboard scheme binds to plain F12, when that is not Edit.GoToDefinition.</summary>
        internal static readonly System.Collections.Generic.List<(Guid Group, uint Id, string Name)> F12Commands = new System.Collections.Generic.List<(Guid, uint, string)>();

        private static object Get(object target, string name) => target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);
        private static bool OnF12(object command) => Get(command, "Bindings") is object[] bindings && bindings.OfType<string>().Any(b => b.EndsWith("::F12", StringComparison.OrdinalIgnoreCase));

        /// <summary>Keyboard schemes can move F12 to another command; handle whichever definition command owns the key.</summary>
        internal static void FindF12(object commandList)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Some SSMS builds have no Edit.GoToDefinition, so a failed lookup falls through to the scan.
                try
                {
                    var standard = commandList.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, commandList, new object[] { "Edit.GoToDefinition", -1 });
                    // ponytail: the full scan touches every command, so it runs only when the usual binding is missing.
                    if (OnF12(standard)) { ActivityLog.TryLogInformation("Querywright", "F12 bound to: Edit.GoToDefinition"); return; }
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { }
                // ponytail: Count/Item(i) rather than foreach; the COM enumerator can throw part way on some SSMS builds.
                int count = (int)Get(commandList, "Count");
                for (int i = 1; i <= count; i++)
                {
                    // Whatever owns plain F12 is handled; TryGoToDefinition passes it on when there is nothing to go to.
                    try
                    {
                        var command = commandList.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, commandList, new object[] { i, -1 });
                        if (OnF12(command)) F12Commands.Add((new Guid((string)Get(command, "Guid")), (uint)(int)Get(command, "ID"), Get(command, "Name") as string ?? ""));
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { }
                }
                ActivityLog.TryLogInformation("Querywright", "F12 bound to: " + string.Join(", ", F12Commands.Select(c => c.Name)));
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "F12 binding lookup failed: " + ((error as System.Reflection.TargetInvocationException)?.InnerException ?? error).GetType().Name);
            }
        }

        private static bool IsDefinition(Guid group, uint id) =>
            (group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn) || F12Commands.Any(c => c.Group == group && c.Id == id);

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Keep F12 enabled in SQL editors even when SSMS's language service disables Go To Definition.
            if (cCmds == 1 && package.Options?.GoToDefinition != false && IsDefinition(pguidCmdGroup, prgCmds[0].cmdID))
            {
                try
                {
                    package.GetSqlView();
                    prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                    return VSConstants.S_OK;
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { }
            }
            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // NOTSUPPORTED passes the command on to SSMS; S_OK swallows it.
            // F12 comes here too: SSMS's language service claims GotoDefn before editor filters see it once connected.
            if (package.Options?.GoToDefinition != false && IsDefinition(pguidCmdGroup, nCmdID))
            {
                SelfTest.Note = "f12 priority";
                try
                {
                    bool handled = package.TryGoToDefinition(package.GetSqlView());
                    // No query text: just which path F12 took, so a user's ActivityLog shows why nothing opened.
                    ActivityLog.TryLogInformation("Querywright", "F12 (" + pguidCmdGroup + ":" + nCmdID + "): " + SelfTest.Note);
                    return handled ? VSConstants.S_OK : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
                }
                catch (InvalidOperationException) { SelfTest.Note = "f12 no view"; return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED; }
                catch (Exception error) when (!(error is OutOfMemoryException)) { EditorCommandFilter.Swallowed(error); return VSConstants.S_OK; }
            }
            const int pass = (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.GOTOBRACE && package.Options?.JumpToBlockPartner != false)
            {
                // Ctrl+]: BEGIN <-> END; brackets and anything else go to SSMS.
                try { return GoToPartner(package.GetSqlView()) ? VSConstants.S_OK : pass; }
                catch (InvalidOperationException) { return pass; }
                catch (Exception error) when (!(error is OutOfMemoryException)) { EditorCommandFilter.Swallowed(error); return pass; }
            }
            // File > Save formats the active SQL document even when focus is elsewhere; Save All formats every unsaved SQL document.
            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && package.Options?.FormatOnSave == true
                && (nCmdID == (uint)VSConstants.VSStd97CmdID.SaveProjectItem || nCmdID == (uint)VSConstants.VSStd97CmdID.SaveSolution))
            {
                System.Collections.Generic.IEnumerable<Microsoft.VisualStudio.Text.Editor.IWpfTextView> views;
                try { views = nCmdID == (uint)VSConstants.VSStd97CmdID.SaveSolution ? EditorListener.UnsavedViews() : new[] { package.SavedSqlView() }.Where(v => v != null); }
                catch (InvalidOperationException) { return pass; }
                foreach (var view in views)
                {
                    try { package.FormatBeforeSave(view); }
                    catch (InvalidOperationException) { }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { EditorCommandFilter.Swallowed(error); }
                }
                return pass;
            }
            if (pguidCmdGroup != group || nCmdID != id) return pass;
            try
            {
                var view = package.GetSqlView();
                var options = package.Options;
                var connection = options == null || string.IsNullOrWhiteSpace(options.ProductionServers) ? null : LiveMetadata.CaptureNames();
                bool production = connection != null && ColorRules.Matches(options.ProductionServers, connection.Value.Server, connection.Value.Database);
                if (options != null && (production || options.WarnUnfilteredChanges || options.WarnDropTruncate || options.WarnUseSwitch))
                {
                    // SSMS runs the selection when there is one, otherwise the whole window.
                    string sql = view.Selection.IsEmpty ? view.TextSnapshot.GetText()
                        : string.Join("\n", view.Selection.SelectedSpans.Select(s => s.GetText()));
                    var targets = sql.Length > 1_000_000 ? System.Array.Empty<string>()
                        : SqlAnalysis.UnfilteredChanges(sql, production || options.WarnUnfilteredChanges, production || options.WarnDropTruncate, production);
                    if (targets.Count > 0 && !Ask(targets, false, production ? connection.Value.Server + (string.IsNullOrEmpty(connection.Value.Database) ? "" : "/" + connection.Value.Database) : null)) return VSConstants.S_OK;
                    var switches = options.WarnUseSwitch && sql.Length <= 1_000_000 ? SqlAnalysis.DatabaseSwitchChanges(sql) : System.Array.Empty<string>();
                    if (switches.Count > 0 && !Ask(switches, true)) return VSConstants.S_OK;
                }
                // Tab history keeps an executed version, like SQL Prompt's ▶ entries.
                if (view.Properties.TryGetProperty("QuerywrightHistory", out Action<bool> save)) save(true);
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { if (!(error is InvalidOperationException)) EditorCommandFilter.Swallowed(error); }
            return pass;
        }

        private static bool GoToPartner(Microsoft.VisualStudio.Text.Editor.IWpfTextView view)
        {
            var snapshot = view.TextSnapshot;
            int caret = view.Caret.Position.BufferPosition.Position;
            var block = BlockCache.At(BlockCache.For(view.TextBuffer).Current(snapshot), caret);
            if (block == null) return false;
            int target = BlockCache.On(caret, block.CloseStart, block.CloseLength) ? (block.HeaderStart >= 0 ? block.HeaderStart : block.OpenStart) : block.CloseStart;
            var point = new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, target);
            view.Caret.MoveTo(point);
            view.ViewScroller.EnsureSpanVisible(new Microsoft.VisualStudio.Text.SnapshotSpan(point, 0), Microsoft.VisualStudio.Text.Editor.EnsureSpanVisibleOptions.AlwaysCenter);
            return true;
        }

        /// <summary>Asks before running; <paramref name="production"/> names the production connection, whose prompt cannot be turned off here.</summary>
        private bool Ask(System.Collections.Generic.IReadOnlyList<string> targets, bool switched, string production = null)
        {
            using (var form = new Form
            {
                Text = "Querywright: execution warning", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
                Font = System.Drawing.SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi,
            })
            using (var bold = new System.Drawing.Font(form.Font, System.Drawing.FontStyle.Bold))
            {
                var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0),
                    Text = switched ? "This script switches database with USE and then changes data:"
                        : production != null ? "PRODUCTION (" + production + "): you're about to execute " + (targets.Count == 1 ? "a statement" : targets.Count + " statements") + " that change schema or can change or remove every row:"
                        : "You're about to execute " + (targets.Count == 1 ? "a statement" : targets.Count + " statements") + " that can change or remove every row:" });
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0), Font = bold,
                    Text = string.Join(Environment.NewLine, targets.Take(10)) + (targets.Count > 10 ? Environment.NewLine + "..." : ""), Margin = new Padding(3, 8, 3, 12) });
                var never = new CheckBox { AutoSize = true, Text = "Don't show this warning again" };
                if (production == null) layout.Controls.Add(never);
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
                bool execute = DialogParts.ShowModal(form) == DialogResult.OK;
                if (never.Checked)
                {
                    if (switched) package.Options.WarnUseSwitch = false;
                    else { package.Options.WarnUnfilteredChanges = false; package.Options.WarnDropTruncate = false; }
                    package.Options.SaveSettingsToStorage();
                }
                return execute;
            }
        }
    }
}
