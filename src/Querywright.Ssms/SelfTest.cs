#nullable enable
using System;
using System.IO;
using System.Linq;
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
        internal static Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.IAsyncCompletionBroker? Completion;
        /// <summary>Last point a traced feature reached; the "note" step prints it. Never logged.</summary>
        internal static string? Note;

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
                        case "dialogs":
                            var dialog = new PromptDialog("Querywright: theme check", "_Name:", "Example_Query");
                            try
                            {
                                if (!(dialog.TryFindResource(VsResourceKeys.ThemedDialogDefaultStylesKey) is System.Windows.ResourceDictionary))
                                    throw new InvalidOperationException("SSMS themed dialog styles unavailable");
                                var expected = dialog.TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey);
                                if (expected == null || !Equals(dialog.Background, expected))
                                    throw new InvalidOperationException("Dialog background does not match SSMS theme");
                                var content = (System.Windows.FrameworkElement)dialog.Content;
                                var size = new System.Windows.Size(456, 240);
                                content.Measure(size); content.Arrange(new System.Windows.Rect(size)); content.UpdateLayout();
                                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(456, 240, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                bitmap.Render(content);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                                using (var file = File.Create(Path.Combine(Path.GetDirectoryName(result)!, "themed-dialog.png"))) encoder.Save(file);
                            }
                            finally { dialog.Close(); }
                            break;
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
                        case "popup": // wait up to 10 s for the suggestion list, so a slow runner doesn't Tab before it opens
                            for (int i = 0; i < 100 && Completion?.IsCompletionActive(view) != true; i++) { await Task.Delay(100); await package.JoinableTaskFactory.SwitchToMainThreadAsync(); }
                            if (Completion?.IsCompletionActive(view) != true) trace.AppendLine("popup: not open after 10 s");
                            break;
                        case "tab": Exec(target, VSConstants.VSStd2K, (uint)VSConstants.VSStd2KCmdID.TAB); break;
                        case "focus": // a keypress lands in the focused editor; first-run dialogs can steal focus
                            view.VisualElement.Focus();
                            await Task.Delay(500);
                            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                            break;
                        case "f12": // through the shell's command routing, as the key is, so priority targets see it
                            Exec(await DispatcherAsync(package), VSConstants.GUID_VSStandardCommandSet97, (uint)VSConstants.VSStd97CmdID.GotoDefn);
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
                        case "keys": // which commands own F12 in this keyboard scheme
                            var dte = await package.GetServiceAsync(typeof(SDTE)) ?? throw new InvalidOperationException("no DTE");
                            var bound = new System.Collections.Generic.List<string>();
                            foreach (object item in (System.Collections.IEnumerable)dte.GetType().InvokeMember("Commands", System.Reflection.BindingFlags.GetProperty, null, dte, null))
                            {
                                var bindings = item.GetType().InvokeMember("Bindings", System.Reflection.BindingFlags.GetProperty, null, item, null) as object[];
                                if (bindings?.OfType<string>().Any(b => b.EndsWith("::F12", StringComparison.OrdinalIgnoreCase)) == true)
                                    bound.Add(item.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null) + " " + string.Join("/", bindings));
                            }
                            view.TextBuffer.Insert(view.TextSnapshot.Length, "\r\n-- keys " + string.Join("; ", bound));
                            break;
                        case "set": // an option for this session only (not saved), e.g. set:FormatOnSave=True
                            var property = typeof(WorkbenchOptions).GetProperty(arg.Substring(0, arg.IndexOf('='))) ?? throw new ArgumentException("unknown option " + arg);
                            property.SetValue(package.Options, Convert.ChangeType(arg.Substring(arg.IndexOf('=') + 1), property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        case "save": // File > Save through the shell's routing, as Ctrl+S is
                            Exec(await DispatcherAsync(package), VSConstants.GUID_VSStandardCommandSet97, (uint)VSConstants.VSStd97CmdID.SaveProjectItem);
                            break;
                        case "prompt": // the next Querywright warning is answered with its Cancel button; its text is appended
                            var answered = view;
                            _ = package.JoinableTaskFactory.RunAsync(async () =>
                            {
                                for (int i = 0; i < 40; i++)
                                {
                                    await Task.Delay(500);
                                    await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                                    var form = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f => f.Text.StartsWith("Querywright"));
                                    if (form == null) continue;
                                    string shown = string.Join(" ", Descendants(form).OfType<System.Windows.Forms.Label>().Select(l => l.Text));
                                    form.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                                    answered.TextBuffer.Insert(answered.TextSnapshot.Length, "\r\n-- prompt " + shown.Replace("\r", " ").Replace("\n", " "));
                                    return;
                                }
                            });
                            break;
                        case "note": view.TextBuffer.Insert(view.TextSnapshot.Length, "\r\n-- note " + (Note ?? "(none)")); break;
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

        private static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent) =>
            parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

        private static void Move(IWpfTextView view, int position)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            view.Selection.Clear();
            view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Max(0, Math.Min(position, view.TextSnapshot.Length))));
        }

        private static async Task<IOleCommandTarget> DispatcherAsync(WorkbenchPackage package)
        {
            var service = await package.GetServiceAsync(typeof(SUIHostCommandDispatcher));
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            return service as IOleCommandTarget ?? throw new InvalidOperationException("no command dispatcher");
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
