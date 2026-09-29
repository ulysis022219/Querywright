#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Querywright.Ssms
{
    /// <summary>
    /// CI end-to-end driver. Off unless QUERYWRIGHT_SELFTEST names a result file. Sends the same editor commands a
    /// keypress produces (TYPECHAR, TAB, F12) through the SQL view's command chain, so the test does not depend on
    /// window focus, then writes the buffer text to the result file. The results-grid steps (exec, grid, cmd) run a query
    /// only when the test script asks for them, against the disposable runner's LocalDB.
    /// </summary>
    internal static class SelfTest
    {
        internal static IVsTextView? Adapter;
        internal static IWpfTextView? View;

        internal static async Task RunAsync(WorkbenchPackage package)
        {
            string? result = Environment.GetEnvironmentVariable("QUERYWRIGHT_SELFTEST");
            if (string.IsNullOrEmpty(result)) return;
            string steps = Environment.GetEnvironmentVariable("QUERYWRIGHT_SELFTEST_STEPS") ?? "";
            try
            {
                for (int i = 0; i < 180 && View == null; i++) await Task.Delay(1000);
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (View == null || Adapter == null) throw new InvalidOperationException("no SQL editor opened");
                var view = View;
                var adapter = Adapter;
                var trace = new System.Text.StringBuilder();
                var target = (IOleCommandTarget)adapter;
                foreach (string step in steps.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = step.IndexOf(':');
                    string name = colon < 0 ? step : step.Substring(0, colon), arg = colon < 0 ? "" : step.Substring(colon + 1);
                    switch (name)
                    {
                        case "wait": await Task.Delay(int.Parse(arg)); await package.JoinableTaskFactory.SwitchToMainThreadAsync(); break;
                        case "home": Move(view, 0); break;
                        case "end": Move(view, view.TextSnapshot.Length); break;
                        case "right": Move(view, view.Caret.Position.BufferPosition.Position + int.Parse(arg)); break;
                        case "left": Move(view, view.Caret.Position.BufferPosition.Position - int.Parse(arg)); break;
                        case "type":
                            foreach (char c in arg)
                            {
                                Type(target, c);
                                // Yield between keys like real typing, so the async completion session can filter.
                                await Task.Delay(80);
                                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                            }
                            break;
                        case "tab": Exec(target, VSConstants.VSStd2K, (uint)VSConstants.VSStd2KCmdID.TAB); break;
                        case "f12": // through the shell's command routing, as the key is, so priority targets see it
                            Exec((IOleCommandTarget)await package.GetServiceAsync(typeof(SUIHostCommandDispatcher)), VSConstants.GUID_VSStandardCommandSet97, (uint)VSConstants.VSStd97CmdID.GotoDefn);
                            break;
                        case "ready": // wait until the window is connected and live metadata has loaded
                            for (int i = 0; i < 150 && !(package.CurrentTables()?.Count > 0); i++) { await Task.Delay(1000); await package.JoinableTaskFactory.SwitchToMainThreadAsync(); }
                            break;
                        case "exec": await RunDteCommandAsync(package, "Query.Execute"); break;
                        case "grid": FocusGrid(); break;
                        case "cmd":
                            var commands = await package.GetServiceAsync(typeof(System.ComponentModel.Design.IMenuCommandService)) as OleMenuCommandService;
                            var command = commands?.FindCommand(new System.ComponentModel.Design.CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), Convert.ToInt32(arg, 16)))
                                ?? throw new InvalidOperationException("command " + arg + " not registered");
                            command.Invoke();
                            break;
                        case "caption": // append the bottom connection label's text
                            view.TextBuffer.Insert(view.TextSnapshot.Length, "\r\n-- " +
                                (view.Properties.TryGetProperty("QuerywrightConnection", out string caption) ? caption : "(none)"));
                            break;
                        case "oe": // right-click the first server in Object Explorer and report the menu
                            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                            view.TextBuffer.Insert(view.TextSnapshot.Length, "\r\n-- oe " + await ServerColorMenu.ProbeAsync(package));
                            break;
                        case "latest": // follow the newest SQL window, e.g. one a command opened
                            if (View == null || Adapter == null) throw new InvalidOperationException("no SQL editor");
                            view = View; adapter = Adapter; target = (IOleCommandTarget)adapter;
                            break;
                        default: throw new ArgumentException("unknown step " + name);
                    }
                    trace.AppendLine(step + " => " + view.TextSnapshot.GetText().Replace("\r\n", "\\n"));
                }
                File.WriteAllText(result + ".trace", trace.ToString());
                File.WriteAllText(result, view.TextSnapshot.GetText());
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                var inner = error is System.Reflection.TargetInvocationException { InnerException: { } cause } ? cause : error;
                File.WriteAllText(result, "error: " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        private static async Task RunDteCommandAsync(WorkbenchPackage package, string command)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await package.GetServiceAsync(typeof(SDTE)) ?? throw new InvalidOperationException("no DTE");
            dte.GetType().InvokeMember("ExecuteCommand", System.Reflection.BindingFlags.InvokeMethod, null, dte, new object[] { command, "" });
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

        /// <summary>Gives keyboard focus to the first visible results grid, as a click would.</summary>
        private static void FocusGrid()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            System.Windows.Forms.Control? grid = null;
            EnumChildWindows(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle, (hwnd, _) =>
            {
                var control = System.Windows.Forms.Control.FromHandle(hwnd);
                for (var type = control?.GetType(); type != null && grid == null; type = type.BaseType)
                    if (type.Name == "GridControl" && control!.Visible) grid = control;
                return grid == null;
            }, IntPtr.Zero);
            if (grid == null) throw new InvalidOperationException("no results grid");
            grid.Focus();
            if (ResultsGridReader.FocusedGrid() != grid) throw new InvalidOperationException("results grid did not take focus");
        }

        private static void Move(IWpfTextView view, int position)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view.Selection.Clear();
            view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Max(0, Math.Min(position, view.TextSnapshot.Length))));
        }

        private static void Exec(IOleCommandTarget target, Guid group, uint id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(target.Exec(ref group, id, 0, IntPtr.Zero, IntPtr.Zero));
        }

        private static void Type(IOleCommandTarget target, char c)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var group = VSConstants.VSStd2K;
            IntPtr input = Marshal.AllocCoTaskMem(16); // VARIANT
            try
            {
                Marshal.GetNativeVariantForObject((ushort)c, input);
                ErrorHandler.ThrowOnFailure(target.Exec(ref group, (uint)VSConstants.VSStd2KCmdID.TYPECHAR, 0, input, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(input); }
        }
    }
}
