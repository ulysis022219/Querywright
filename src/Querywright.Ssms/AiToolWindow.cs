using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    [Guid("5d0f7c1e-93a4-4b6e-8a27-c1f4e6b90d32")]
    public sealed class AiToolWindow : ToolWindowPane
    {
        public AiToolWindow() : base(null) { Caption = "Querywright AI"; Content = new AiPanel(); }
        internal AiPanel Panel => (AiPanel)Content;
    }

    internal sealed class AiPanel : DockPanel
    {
        private static readonly (string Label, AiTask Task)[] Modes =
        {
            ("Explain", AiTask.Explain), ("Fix", AiTask.Fix), ("Optimize", AiTask.Optimize),
            ("Change selection", AiTask.Edit), ("Write query", AiTask.Generate)
        };
        private static readonly string HistoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "ai-prompts.json");
        private readonly TextBox prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(8), FontSize = 14 };
        private readonly TextBlock status = DialogParts.Status();
        private readonly StackPanel transcript = new StackPanel();
        private readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly List<ToggleButton> chips = new List<ToggleButton>();
        private readonly List<AiMessage> conversation = new List<AiMessage>();
        private readonly List<string> prompts = new List<string>();
        private readonly Button send, stop, fresh;
        private readonly TextBlock context = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };
        private readonly Button scope, regenerate, editLast;
        private readonly CheckBox schema = new CheckBox { Content = "Include schema", IsChecked = true, Margin = new Thickness(4), ToolTip = "Include table definitions with the next message. Earlier conversation context is still sent." };
        private bool wholeScript, editing;
        private string lastPrompt, lastContext;
        private AiTask lastTask;
        private IReadOnlyList<AiMessage> lastRequest;
        private List<AiMessage> beforeLast;
        private StackPanel lastBody;
        private UIElement lastUser;
        private CancellationTokenSource running;
        private AiTask? selected;
        private bool followBottom = true;

        internal AiPanel()
        {
            Margin = new Thickness(12); LastChildFill = true; UseLayoutRounding = true;
            SetValue(TextElement.FontFamilyProperty, SystemFonts.MessageFontFamily);
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            if (TryFindResource(VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary theme) Resources.MergedDictionaries.Add(theme);
            StyleButtons(typeof(Button)); StyleButtons(typeof(ToggleButton));
            prompt.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            prompt.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            schema.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AutomationProperties.SetName(prompt, "AI prompt. Enter sends; Shift+Enter adds a line; Up recalls previous prompt.");

            fresh = Button("New conversation", "Clear this conversation", () =>
            {
                conversation.Clear(); transcript.Children.Clear(); status.Text = ""; prompt.Clear(); selected = null;
                lastRequest = null; lastBody = null; lastUser = null; beforeLast = null; editing = false; regenerate.IsEnabled = editLast.IsEnabled = false;
                followBottom = true; UpdateChips(); Welcome(); prompt.Focus(); RefreshContext();
            });
            regenerate = Button("Regenerate", "Retry the last request with its original context", () => Send(null, retry: true));
            editLast = Button("Edit last prompt", "Edit and resend the last prompt, replacing its reply", () =>
            {
                prompt.Text = lastPrompt; prompt.CaretIndex = prompt.Text.Length; selected = lastTask == AiTask.Chat ? (AiTask?)null : lastTask;
                editing = true; UpdateChips(); prompt.Focus(); status.Text = "Editing last prompt. Send replaces its reply.";
            });
            regenerate.IsEnabled = editLast.IsEnabled = false;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock { Text = "Querywright AI", FontSize = 16, FontWeight = FontWeights.SemiBold });
            var navigation = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            navigation.Children.Add(fresh); navigation.Children.Add(regenerate); navigation.Children.Add(editLast); header.Children.Add(navigation);
            Dock(header, System.Windows.Controls.Dock.Top);

            var composer = new StackPanel();
            composer.Children.Add(context);
            scope = Button("Whole script", "Toggle between the selection and the whole editor script", () => { wholeScript = !wholeScript; RefreshContext(); });
            var contextActions = new WrapPanel(); contextActions.Children.Add(scope); contextActions.Children.Add(schema);
            composer.Children.Add(contextActions);
            schema.Click += (s, e) => RefreshContext();
            var contextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            contextTimer.Tick += (s, e) => { if (running == null) RefreshContext(); };
            Loaded += (s, e) => { RefreshContext(); contextTimer.Start(); };
            Unloaded += (s, e) => { contextTimer.Stop(); running?.Cancel(); };
            var quick = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            foreach (var mode in Modes)
            {
                var chip = new ToggleButton { Content = mode.Label, Tag = mode.Task, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), MinHeight = 30, ToolTip = "Use " + mode.Label + " for the next message" };
                chip.Click += (s, e) => { selected = chip.IsChecked == true ? (AiTask?)mode.Task : null; editing = false; UpdateChips(); prompt.Focus(); };
                chips.Add(chip); quick.Children.Add(chip);
            }
            composer.Children.Add(quick);
            send = Button("Send", "Enter or Ctrl+Enter sends", () => Send(null)); send.FontWeight = FontWeights.SemiBold;
            stop = Button("Stop", "Cancel and keep any partial answer", () => running?.Cancel()); stop.IsEnabled = false;
            var input = new DockPanel();
            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            buttons.Children.Add(send); buttons.Children.Add(stop); SetDock(buttons, System.Windows.Controls.Dock.Right);
            input.Children.Add(buttons); input.Children.Add(prompt);
            var frame = Frame(input); frame.CornerRadius = new CornerRadius(10);
            composer.Children.Add(frame); composer.Children.Add(status);
            Dock(composer, System.Windows.Controls.Dock.Bottom);
            scroll.Content = transcript; Children.Add(scroll);
            scroll.ScrollChanged += (s, e) => { if (e.ExtentHeightChange == 0 && e.VerticalChange != 0) followBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 24; };
            prompt.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) { e.Handled = true; Send(null); }
                else if (e.Key == Key.Up && prompt.Text.Length == 0 && prompts.Count > 0) { e.Handled = true; prompt.Text = prompts[0]; prompt.CaretIndex = prompt.Text.Length; }
            };
            LoadHistory(); Welcome();
        }

        private void Dock(UIElement element, Dock side) { SetDock(element, side); Children.Add(element); }
        private void StyleButtons(Type type)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(content);
            var style = new Style(type, TryFindResource(type) as Style);
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(type) { VisualTree = border }));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ComboBoxBackgroundBrushKey)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusedProperty })
            {
                var focus = new Trigger { Property = property, Value = true };
                focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
                style.Triggers.Add(focus);
            }
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.5)); style.Triggers.Add(disabled);
            if (type == typeof(ToggleButton))
            {
                var chosen = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                chosen.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
                chosen.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); style.Triggers.Add(chosen);
            }
            Resources[type] = style;
        }
        private void RefreshContext()
        {
            if (WorkbenchPackage.Instance == null) { context.Text = "Open a SQL query to include editor context."; return; }
            try
            {
                var preview = WorkbenchPackage.Instance.AiContextPreview(prompt.Text, wholeScript, schema.IsChecked == true);
                context.Text = preview.Where + " | Schema: " + preview.Tables + " tables (cached preview)";
                scope.Content = preview.Scope;
                AutomationProperties.SetName(scope, preview.Scope + ". Toggle editor scope.");
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { context.Text = "Context unavailable: " + Problem(error); }
        }
        private void UpdateChips() { foreach (var chip in chips) chip.IsChecked = selected == (AiTask)chip.Tag; }
        private static Border Frame(UIElement child)
        {
            var frame = new Border { Child = child, Padding = new Thickness(8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return frame;
        }
        private Button Button(string text, string tip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, text);
            button.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { status.Text = Problem(error); }
            };
            return button;
        }

        private void Welcome()
        {
            var welcome = new StackPanel { Margin = new Thickness(4, 18, 4, 18), Tag = "welcome" };
            welcome.Children.Add(new TextBlock { Text = "What are you working on?", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            welcome.Children.Add(new TextBlock { Text = "Write SQL, understand a query, or refine an answer. Review generated SQL before using it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14), FontSize = 14 });
            foreach (string example in new[] { "Write a query to find duplicate rows", "Explain this query step by step", "Help me make this query faster" })
                welcome.Children.Add(Button(example, "Put this example in the prompt", () => { prompt.Text = example; prompt.Focus(); }));
            transcript.Children.Add(welcome);
        }

        private StackPanel Message(string role, string text)
        {
            if (transcript.Children.Count == 1 && (transcript.Children[0] as FrameworkElement)?.Tag as string == "welcome") transcript.Children.Clear();
            var message = new StackPanel { Margin = new Thickness(role == "You" ? 32 : 0, 10, 0, 12) };
            message.Children.Add(new TextBlock { Text = role, FontWeight = FontWeights.SemiBold, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 6) });
            var body = new StackPanel();
            if (role == "You")
            {
                body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 });
                var bubble = Frame(body); bubble.HorizontalAlignment = HorizontalAlignment.Right; message.Children.Add(bubble);
            }
            else { message.Children.Add(body); Render(body, text); }
            transcript.Children.Add(message); KeepBottom();
            return body;
        }

        private void KeepBottom() { if (followBottom) { scroll.UpdateLayout(); scroll.ScrollToEnd(); } }

        private RichTextBox Readable(FlowDocument document)
        {
            document.PagePadding = new Thickness(0); document.FontSize = 14; document.LineHeight = 22;
            var box = new RichTextBox { Document = document, IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent };
            box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return box;
        }

        private void Render(StackPanel body, string text)
        {
            body.Children.Clear();
            var document = new FlowDocument();
            foreach (var block in AiMarkdown.Parse(text))
            {
                if (block.Kind == AiBlockKind.Code)
                {
                    var code = new StackPanel();
                    var actions = new WrapPanel();
                    actions.Children.Add(new TextBlock { Text = block.Language.Length > 0 ? block.Language.ToUpperInvariant() : "SQL", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 12, 6) });
                    actions.Children.Add(Button("Copy", "Copy this code block", () => { Clipboard.SetText(block.Text); status.Text = "Code copied."; }));
                    if (block.IsSql && block.Closed && !string.IsNullOrWhiteSpace(block.Text))
                    {
                        actions.Children.Add(SqlButton("Insert", () => WorkbenchPackage.Instance.AiInsertAsync(block.Text, false)));
                        actions.Children.Add(SqlButton("Replace", () => WorkbenchPackage.Instance.AiInsertAsync(block.Text, true)));
                        actions.Children.Add(SqlButton("New window", () => WorkbenchPackage.Instance.AiOpenAsync(block.Text, false)));
                        actions.Children.Add(SqlButton("Run", () => WorkbenchPackage.Instance.AiOpenAsync(block.Text, true)));
                    }
                    code.Children.Add(actions);
                    var sqlDocument = new FlowDocument { FontFamily = new FontFamily("Consolas, Courier New") };
                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    IReadOnlyList<(string Text, string Kind)> tokens = block.IsSql ? AiMarkdown.SqlTokens(block.Text) : new[] { (Text: block.Text, Kind: "text") };
                    foreach (var token in tokens)
                    {
                        var run = new Run(token.Text);
                        if (token.Kind == "keyword") { run.FontWeight = FontWeights.SemiBold; run.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.PanelHyperlinkBrushKey); }
                        else if (token.Kind == "string") run.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.PanelHyperlinkBrushKey);
                        else if (token.Kind == "comment") { run.FontStyle = FontStyles.Italic; run.SetResourceReference(TextElement.ForegroundProperty, SystemColors.GrayTextBrushKey); }
                        paragraph.Inlines.Add(run);
                    }
                    sqlDocument.Blocks.Add(paragraph); code.Children.Add(Readable(sqlDocument));
                    document.Blocks.Add(new BlockUIContainer(Frame(code)) { Margin = new Thickness(0, 4, 0, 12) });
                    continue;
                }
                var prose = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                if (block.Kind == AiBlockKind.Heading) { prose.FontSize = Math.Max(15, 23 - block.Level * 2); prose.FontWeight = FontWeights.SemiBold; }
                if (block.Kind == AiBlockKind.Table) { prose.FontFamily = new FontFamily("Consolas, Courier New"); prose.Inlines.Add(new Run(block.Text)); }
                else
                {
                    if (block.Kind == AiBlockKind.ListItem) { prose.Margin = new Thickness(12, 0, 0, 6); prose.Inlines.Add(new Run(block.Prefix + " ")); }
                    foreach (var item in block.Inlines)
                    {
                        var run = new Run(item.Text);
                        if (item.Bold) run.FontWeight = FontWeights.Bold;
                        if (item.Italic) run.FontStyle = FontStyles.Italic;
                        if (item.Code) { run.FontFamily = new FontFamily("Consolas, Courier New"); run.SetResourceReference(TextElement.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey); }
                        prose.Inlines.Add(run);
                    }
                }
                document.Blocks.Add(prose);
            }
            body.Children.Add(Readable(document)); KeepBottom();
        }

        private Button SqlButton(string text, Func<Task> action) => Button(text, text + " this SQL block", () =>
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); status.Text = Problem(error); }
            }));

        internal void FocusPrompt() => prompt.Focus();
        internal void Start(AiTask task) { selected = task; UpdateChips(); Send(task); }

        private sealed class DeltaBuffer : IProgress<string>
        {
            private readonly StringBuilder text = new StringBuilder();
            private int shown;
            public void Report(string value) { lock (text) text.Append(value); }
            internal string Text { get { lock (text) return text.ToString(); } }
            internal string Take() { lock (text) { string value = text.ToString(shown, text.Length - shown); shown = text.Length; return value; } }
        }

        private void Send(AiTask? forced, bool retry = false)
        {
            if (running != null) return;
            if (retry && lastRequest == null) return;
            var task = retry ? lastTask : forced ?? selected ?? (conversation.Count == 0 ? AiTask.Generate : AiTask.Chat);
            string instruction = retry ? lastPrompt : prompt.Text.Trim();
            if ((task == AiTask.Generate || task == AiTask.Chat || task == AiTask.Edit) && instruction.Length == 0) { status.Text = "Type a prompt first."; prompt.Focus(); return; }
            var cancellation = running = new CancellationTokenSource();
            StackPanel body;
            if (retry) { body = lastBody; Render(body, "Thinking…"); context.Text = lastContext; }
            else
            {
                if (editing && lastBody != null)
                {
                    transcript.Children.Remove(lastUser); transcript.Children.Remove((UIElement)lastBody.Parent);
                    conversation.Clear(); conversation.AddRange(beforeLast);
                }
                beforeLast = conversation.ToList(); editing = false;
                Message("You", instruction.Length > 0 ? instruction : Modes.First(m => m.Task == task).Label);
                lastUser = transcript.Children[transcript.Children.Count - 1];
                body = lastBody = Message("Assistant", "Thinking…");
                lastRequest = null; lastPrompt = instruction; lastTask = task;
            }
            var deltas = new DeltaBuffer();
            var live = new Paragraph { Margin = new Thickness(0) };
            var liveBox = Readable(new FlowDocument(live));
            bool started = false;
            void Flush()
            {
                string delta = deltas.Take();
                if (delta.Length == 0) return;
                if (!started) { body.Children.Clear(); body.Children.Add(liveBox); started = true; }
                live.Inlines.Add(new Run(delta)); KeepBottom();
            }
            // ponytail: append plain text during streaming, format once on completion to avoid markdown/control churn.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (s, e) => Flush(); timer.Start();
            send.IsEnabled = fresh.IsEnabled = prompt.IsEnabled = false; stop.IsEnabled = true;
            scope.IsEnabled = schema.IsEnabled = regenerate.IsEnabled = editLast.IsEnabled = false;
            foreach (var chip in chips) chip.IsEnabled = false;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    string reply = retry
                        ? await WorkbenchPackage.Instance.AiReplyAsync(lastRequest, conversation, cancellation.Token, deltas)
                        : await WorkbenchPackage.Instance.AiAskAsync(task, instruction, conversation, text => status.Text = text, cancellation.Token, deltas, wholeScript, schema.IsChecked == true,
                            (messages, description) => { lastRequest = messages.ToList(); context.Text = lastContext = description; });
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    timer.Stop();
                    Finish(body, reply); status.Text = "Done. Review SQL before using it.";
                    if (instruction.Length > 0) Remember(instruction);
                    prompt.Clear(); selected = null; UpdateChips();
                }
                catch (OperationCanceledException)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); timer.Stop();
                    Finish(body, deltas.Text); body.Children.Add(new TextBlock { Text = "Stopped — partial answer", FontStyle = FontStyles.Italic }); status.Text = "Stopped.";
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); timer.Stop();
                    Finish(body, deltas.Text); body.Children.Add(new TextBlock { Text = Problem(error), TextWrapping = TextWrapping.Wrap }); status.Text = Problem(error);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    timer.Stop();
                    running = null; cancellation.Dispose();
                    send.IsEnabled = fresh.IsEnabled = prompt.IsEnabled = true; stop.IsEnabled = false;
                    scope.IsEnabled = schema.IsEnabled = true; schema.IsChecked = true;
                    regenerate.IsEnabled = editLast.IsEnabled = lastRequest != null;
                    foreach (var chip in chips) chip.IsEnabled = true;
                    prompt.Focus();
                }
            });
        }

        private void Finish(StackPanel body, string reply)
        {
            Render(body, reply);
            if (reply.Length > 0) body.Children.Add(Button("Copy answer", "Copy the whole answer as Markdown", () => { Clipboard.SetText(reply); status.Text = "Answer copied."; }));
            KeepBottom();
        }

        private static string Problem(Exception error)
        {
            var cause = (error as System.Reflection.TargetInvocationException)?.InnerException ?? error;
            if (cause is System.Data.SqlClient.SqlException) return "Could not read from the server: " + WorkbenchPackage.Reason(cause);
            return cause is InvalidOperationException || cause is FormatException || cause is UriFormatException ? cause.Message : "AI request failed: " + cause.GetType().Name + ".";
        }

        private void Remember(string text)
        {
            prompts.RemoveAll(p => p == text); prompts.Insert(0, text);
            if (prompts.Count > 30) prompts.RemoveRange(30, prompts.Count - 30);
            var saved = prompts.ToList();
            // ponytail: prompts persist as plain text for this account, add opt-in storage before persisting full transcripts.
            _ = Task.Run(() =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)); File.WriteAllText(HistoryFile, MiniJson.Write(saved)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            });
        }
        private void LoadHistory()
        {
            try { if (File.Exists(HistoryFile) && MiniJson.Parse(File.ReadAllText(HistoryFile)) is List<object> list) prompts.AddRange(list.OfType<string>().Take(30)); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { }
        }
    }
}
