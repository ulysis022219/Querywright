using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>Querywright > AI assistant (Alt+Z): a dockable window that writes, edits, explains, fixes and optimizes T-SQL
    /// with the provider set under Tools > Options > Querywright > AI.</summary>
    [Guid("5d0f7c1e-93a4-4b6e-8a27-c1f4e6b90d32")]
    public sealed class AiToolWindow : ToolWindowPane
    {
        public AiToolWindow() : base(null)
        {
            Caption = "Querywright AI";
            Content = new AiPanel();
        }

        internal AiPanel Panel => (AiPanel)Content;
    }

    internal sealed class AiPanel : DockPanel
    {
        private static readonly (string Label, AiTask Task, string Hint)[] Modes =
        {
            ("Write a query", AiTask.Generate, "Describe the query to write. The script in the editor and the database schema go along as context."),
            ("Change the selection", AiTask.Edit, "Describe the change. Works on the selected SQL, or the whole script when nothing is selected."),
            ("Explain", AiTask.Explain, "Explains the selected SQL (or the whole script). A question in the box is optional."),
            ("Fix errors", AiTask.Fix, "Asks SQL Server for the script's compile errors (nothing is run) and corrects them."),
            ("Optimize", AiTask.Optimize, "Reads the estimated execution plan (nothing is run) and rewrites the query to be faster."),
            ("Ask / follow up", AiTask.Chat, "Ask anything, or refine the last answer: \"also filter by year\", \"use a CTE instead\"."),
        };

        private static readonly string HistoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "ai-prompts.json");
        private const int HistorySize = 30;

        private readonly ComboBox mode = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox history = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Prompts you sent before" };
        private readonly TextBox prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBox output = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
        private readonly TextBlock hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6), Opacity = 0.8 };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Button send = new Button { Content = "Send", MinWidth = 76, Padding = new Thickness(10, 4, 10, 4), ToolTip = "Ctrl+Enter" };
        private readonly Button stop = new Button { Content = "Stop", MinWidth = 60, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        private readonly List<Button> sqlButtons = new List<Button>();
        private readonly List<AiMessage> conversation = new List<AiMessage>();
        private readonly List<string> prompts = new List<string>();
        private CancellationTokenSource running;
        private string sql;

        internal AiPanel()
        {
            Margin = new Thickness(8);
            LastChildFill = true;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            TextElementForeground(this);
            if (TryFindResource(VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary theme) Resources.MergedDictionaries.Add(theme);

            foreach (var item in Modes) mode.Items.Add(item.Label);
            mode.SelectedIndex = 0;
            mode.SelectionChanged += (s, e) => hint.Text = Modes[Math.Max(0, mode.SelectedIndex)].Hint;
            hint.Text = Modes[0].Hint;
            var fresh = new Button { Content = "New conversation", Padding = new Thickness(10, 4, 10, 4), ToolTip = "Forget the answers so far" };
            fresh.Click += (s, e) => { conversation.Clear(); sql = null; output.Clear(); status.Text = ""; EnableSqlButtons(); prompt.Focus(); };
            history.SelectionChanged += (s, e) =>
            {
                if (history.SelectedIndex < 0) return;
                prompt.Text = prompts[history.SelectedIndex];
                prompt.CaretIndex = prompt.Text.Length;
                history.SelectedIndex = -1;
                prompt.Focus();
            };
            var top = new WrapPanel();
            top.Children.Add(mode); top.Children.Add(history); top.Children.Add(fresh);
            Dock(top, System.Windows.Controls.Dock.Top);
            Dock(hint, System.Windows.Controls.Dock.Top);
            Dock(prompt, System.Windows.Controls.Dock.Top);
            prompt.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                e.Handled = true;
                Send(null);
            };

            send.Click += (s, e) => Send(null);
            stop.Click += (s, e) => running?.Cancel();
            var sendRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            sendRow.Children.Add(send); sendRow.Children.Add(stop);
            Dock(sendRow, System.Windows.Controls.Dock.Top);

            Dock(status, System.Windows.Controls.Dock.Bottom);
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(SqlButton("Insert at caret", "Insert the answer's SQL at the caret of the query window", () => { WorkbenchPackage.Instance.AiInsert(sql, replace: false); return Task.CompletedTask; }));
            actions.Children.Add(SqlButton("Replace in editor", "Replace the selection (the whole script when nothing is selected) with the answer's SQL. Ctrl+Z undoes it.", () => { WorkbenchPackage.Instance.AiInsert(sql, replace: true); return Task.CompletedTask; }));
            actions.Children.Add(SqlButton("New query window", "Open the answer's SQL in a new query window without running it", () => WorkbenchPackage.Instance.AiOpenAsync(sql, run: false)));
            actions.Children.Add(SqlButton("Run", "Open the answer's SQL in a new query window and execute it", () => WorkbenchPackage.Instance.AiOpenAsync(sql, run: true)));
            actions.Children.Add(SqlButton("Copy SQL", "Copy the answer's SQL", () => { Clipboard.SetText(sql); status.Text = "SQL copied."; return Task.CompletedTask; }));
            Dock(actions, System.Windows.Controls.Dock.Bottom);
            EnableSqlButtons();

            output.FontFamily = new FontFamily("Consolas, Courier New");
            Children.Add(output);
            LoadHistory();
        }

        private void Dock(UIElement element, Dock side) { SetDock(element, side); Children.Add(element); }

        private static void TextElementForeground(FrameworkElement element) =>
            element.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        private Button SqlButton(string text, string tip, Func<Task> action)
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (s, e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (string.IsNullOrWhiteSpace(sql)) return;
                try { await action(); }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    status.Text = Problem(error);
                }
            });
            sqlButtons.Add(button);
            return button;
        }

        private void EnableSqlButtons()
        {
            foreach (var button in sqlButtons) button.IsEnabled = !string.IsNullOrWhiteSpace(sql);
        }

        internal void FocusPrompt() => prompt.Focus();

        /// <summary>Menu commands Explain / Fix / Optimize: pick the mode and send at once with whatever is in the prompt box.</summary>
        internal void Start(AiTask task)
        {
            mode.SelectedIndex = Array.FindIndex(Modes, m => m.Task == task);
            Send(task);
        }

        private void Send(AiTask? forced)
        {
            if (running != null) return;
            var task = forced ?? Modes[Math.Max(0, mode.SelectedIndex)].Task;
            string instruction = prompt.Text.Trim();
            var cancellation = running = new CancellationTokenSource();
            send.IsEnabled = false; stop.IsEnabled = true;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    string reply = await WorkbenchPackage.Instance.AiAskAsync(task, instruction, conversation, text => status.Text = text, cancellation.Token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    output.Text = reply;
                    output.ScrollToHome();
                    sql = AiAssist.ExtractSql(reply);
                    status.Text = sql != null ? "Done. Review the SQL before you use it." : "Done.";
                    if (instruction.Length > 0) Remember(instruction);
                    prompt.Clear();
                    // The next thing typed is most likely a refinement of this answer.
                    mode.SelectedIndex = Array.FindIndex(Modes, m => m.Task == AiTask.Chat);
                }
                catch (OperationCanceledException)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    status.Text = "Stopped.";
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    status.Text = Problem(error);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    running = null;
                    cancellation.Dispose();
                    send.IsEnabled = true; stop.IsEnabled = false;
                    EnableSqlButtons();
                }
            });
        }

        private static string Problem(Exception error)
        {
            var cause = (error as System.Reflection.TargetInvocationException)?.InnerException ?? error;
            if (cause is System.Data.SqlClient.SqlException) return "Could not read from the server: " + WorkbenchPackage.Reason(cause);
            return cause is InvalidOperationException || cause is FormatException || cause is UriFormatException ? cause.Message : "AI request failed: " + cause.GetType().Name + ".";
        }

        private void Remember(string text)
        {
            prompts.RemoveAll(p => p == text);
            prompts.Insert(0, text);
            if (prompts.Count > HistorySize) prompts.RemoveRange(HistorySize, prompts.Count - HistorySize);
            ShowHistory();
            var saved = prompts.ToList();
            // ponytail: prompts are kept as plain text for this Windows account; delete the file to clear them.
            _ = Task.Run(() =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)); File.WriteAllText(HistoryFile, MiniJson.Write(saved)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            });
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFile) && MiniJson.Parse(File.ReadAllText(HistoryFile)) is List<object> list)
                    prompts.AddRange(list.OfType<string>().Take(HistorySize));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { }
            ShowHistory();
        }

        private void ShowHistory()
        {
            history.Items.Clear();
            foreach (string text in prompts)
            {
                string line = text.Replace("\r", " ").Replace("\n", " ");
                history.Items.Add(line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            }
            history.IsEnabled = prompts.Count > 0;
            history.Text = "";
        }
    }
}
