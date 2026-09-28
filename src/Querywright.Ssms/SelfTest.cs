#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Querywright.Ssms
{
    /// <summary>
    /// CI end-to-end driver. Off unless QUERYWRIGHT_SELFTEST names a result file. Sends the same editor commands a
    /// keypress produces (TYPECHAR, TAB, F12) through the SQL view's command chain, so the test does not depend on
    /// window focus, then writes the buffer text to the result file. Never touches a database.
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
                var trace = new System.Text.StringBuilder();
                var target = (IOleCommandTarget)Adapter;
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
                        case "f12": Exec(target, VSConstants.GUID_VSStandardCommandSet97, (uint)VSConstants.VSStd97CmdID.GotoDefn); break;
                        default: throw new ArgumentException("unknown step " + name);
                    }
                    trace.AppendLine(step + " => " + view.TextSnapshot.GetText().Replace("\r\n", "\\n"));
                }
                File.WriteAllText(result + ".trace", trace.ToString());
                File.WriteAllText(result, view.TextSnapshot.GetText());
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                File.WriteAllText(result, "error: " + error.GetType().Name + ": " + error.Message);
            }
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
