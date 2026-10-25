using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;

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
        private static readonly (string Label, AiTask Task, string Tip)[] Modes =
        {
            ("Write query", AiTask.Generate, "Write a new query from your description"),
            ("Change selection", AiTask.Edit, "Rewrite the selection the way you describe"),
            ("Explain", AiTask.Explain, "Explain what the selection or script does"),
            ("Fix", AiTask.Fix, "Fix the errors SQL Server reports"),
            ("Optimize", AiTask.Optimize, "Rewrite for speed using the estimated plan"),
            ("Review", AiTask.Review, "Review for bugs, risks and style problems"),
            ("Document", AiTask.Document, "Add a header and step comments"),
            ("Tests", AiTask.Test, "Write a test script that rolls back")
        };
        private static readonly (string Name, string Detail)[] Commands =
        {
            ("write", "Write a new query"), ("edit", "Change the selection"), ("explain", "Explain the selection or script"),
            ("fix", "Fix compile errors"), ("optimize", "Rewrite for speed"), ("review", "Review for problems"),
            ("doc", "Add header and step comments"), ("test", "Write a rollback-only test"), ("ask", "Ask a follow-up question"),
            ("export", "Save this conversation as Markdown"), ("clear", "Start a new conversation"), ("help", "List commands")
        };
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily CodeFont = new FontFamily("Consolas, Courier New");
        private static readonly string HistoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "ai-prompts.json");
        private readonly TextBox prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 0, 2), FontSize = 14, Background = Brushes.Transparent };
        private readonly TextBlock placeholder = new TextBlock { Text = "Ask about this query. Type / for commands or @ for a table.", TextWrapping = TextWrapping.Wrap, FontSize = 14, Opacity = 0.55, IsHitTestVisible = false, Margin = new Thickness(2, 3, 2, 0) };
        private readonly TextBlock status = DialogParts.Status();
        private readonly StackPanel transcript = new StackPanel { Margin = new Thickness(16, 4, 16, 8) };
        private readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly List<ToggleButton> chips = new List<ToggleButton>();
        private readonly List<AiMessage> conversation = new List<AiMessage>();
        private readonly List<string> prompts = new List<string>();
        private readonly Button send, fresh, export, scope, regenerate, editLast, refreshModels, modelButton;
        private readonly TextBlock context = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock modelLabel = new TextBlock { MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock chevron = Icon("", 10), sendGlyph = Icon("", 15);
        private readonly Ellipse connection = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Border drawer;
        private readonly Popup suggest = new Popup { Placement = PlacementMode.Top, StaysOpen = true, AllowsTransparency = true, VerticalOffset = -6 };
        private readonly ListBox suggestions = new ListBox { Focusable = false, MaxHeight = 260, MinWidth = 260, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly ComboBox model = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, MinWidth = 120, ToolTip = "Choose a provider model or type its identifier. Opening the list contacts the provider for model metadata only." };
        private readonly ComboBox reasoning = new ComboBox { Width = 110, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "Off", "Low", "Med", "High" }, SelectedIndex = 0, ToolTip = "Requested reasoning effort. Off omits reasoning fields; provider defaults may still reason." };
        private readonly CheckBox schema = new CheckBox { IsChecked = true, MinHeight = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Include table definitions with the next message. Earlier conversation context is still sent." };
        private readonly Style ghost;
        private string providerIdentity, optionModel;
        private AiReasoningLevel optionReasoning;
        private bool syncingProvider;
        private CancellationTokenSource modelDiscovery;
        private bool wholeScript, editing, connected;
        private string lastPrompt, lastContext, lastWhere;
        private AiTask lastTask;
        private IReadOnlyList<AiMessage> lastRequest;
        private List<AiMessage> beforeLast;
        private StackPanel lastBody;
        private UIElement lastUser;
        private CancellationTokenSource running;
        private AiTask? selected;
        private bool followBottom = true;

        /// <summary>One transcript entry: what export writes, plus the parts that change after it is shown.</summary>
        private sealed class Turn
        {
            internal string Role, Text;
            internal TextBlock Caption;
            internal FrameworkElement Mark;
        }

        internal AiPanel()
        {
            LastChildFill = true; UseLayoutRounding = true;
            SetValue(TextElement.FontFamilyProperty, SystemFonts.MessageFontFamily);
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            if (TryFindResource(VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary theme) Resources.MergedDictionaries.Add(theme);
            // Build every style before registering any, so each is based on the host theme rather than on another panel style.
            Style plain = ButtonStyle(typeof(Button), 4), round = ButtonStyle(typeof(Button), 15), accent = Accent(ButtonStyle(typeof(Button), 18));
            Style chip = ButtonStyle(typeof(ToggleButton), 15), check = ButtonStyle(typeof(CheckBox), 15);
            Resources[typeof(Button)] = plain; Resources[typeof(ToggleButton)] = chip;
            ghost = Ghost(plain); schema.Style = check;
            var itemStyle = new Style(typeof(ComboBoxItem), TryFindResource(typeof(ComboBoxItem)) as Style);
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ComboBoxBackgroundBrushKey)));
            Resources[typeof(ComboBoxItem)] = itemStyle;

            // Header: the model switch, then conversation tools. The tool window caption already names the panel.
            fresh = IconButton("", "New conversation", "Clear this conversation and start over", NewConversation);
            export = IconButton("", "Export conversation", "Save this conversation as a Markdown file", Export);
            var logs = IconButton("", "Logs", "View, export or import Querywright diagnostic logs", () =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async () => await WorkbenchPackage.Instance.ShowDialogAsync(new DiagnosticsDialog()));
            });
            modelLabel.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            chevron.Margin = new Thickness(6, 1, 0, 0);
            var modelContent = new StackPanel { Orientation = Orientation.Horizontal };
            modelContent.Children.Add(modelLabel); modelContent.Children.Add(chevron);
            modelButton = Button("Model settings", "Model and reasoning settings", ToggleDrawer);
            modelButton.Content = modelContent; modelButton.Padding = new Thickness(8, 0, 8, 0); modelButton.Margin = new Thickness(-8, 0, 8, 0);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tool in new[] { fresh, export, logs }) tools.Children.Add(tool);
            var header = new DockPanel { Margin = new Thickness(16, 8, 10, 6), LastChildFill = false };
            SetDock(tools, System.Windows.Controls.Dock.Right); header.Children.Add(tools); header.Children.Add(modelButton);
            Place(header, System.Windows.Controls.Dock.Top);

            // Drawer: provider model and reasoning, hidden until the model switch opens it.
            refreshModels = IconButton("", "Refresh models", "Reload provider model metadata. Sends no SQL or prompt.", () => LoadModels(true));
            refreshModels.Margin = new Thickness(4, 0, 0, 0);
            var settings = Button("Provider settings", "Open Querywright AI options: provider, key and limits", () => WorkbenchPackage.Instance?.ShowOptionPage(typeof(AiOptions)));
            settings.Margin = new Thickness(8, 0, 0, 0);
            var modelRow = new DockPanel();
            SetDock(refreshModels, System.Windows.Controls.Dock.Right); modelRow.Children.Add(refreshModels); modelRow.Children.Add(model);
            var reasoningRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            SetDock(settings, System.Windows.Controls.Dock.Right); reasoningRow.Children.Add(settings); reasoningRow.Children.Add(reasoning);
            var fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(fields, new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, 0, 0);
            Cell(fields, modelRow, 0, 1);
            Cell(fields, new TextBlock { Text = "Reasoning", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 0) }, 1, 0);
            Cell(fields, reasoningRow, 1, 1);
            drawer = Card(fields, 8); drawer.Padding = new Thickness(12, 10, 10, 10); drawer.Margin = new Thickness(16, 0, 16, 8); drawer.Visibility = Visibility.Collapsed;
            Place(drawer, System.Windows.Controls.Dock.Top);
            foreach (var picker in new[] { model, reasoning })
            {
                picker.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
                picker.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                picker.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                picker.Loaded += (s, e) => ThemePicker(picker);
            }
            AutomationProperties.SetName(model, "AI model. Choose or type model identifier."); AutomationProperties.SetName(reasoning, "Requested reasoning effort");
            model.DropDownOpened += (s, e) => LoadModels(false);
            model.SelectionChanged += (s, e) => { if (!syncingProvider && model.SelectedItem is string choice) { model.Text = choice; SaveProvider(); } };
            model.LostKeyboardFocus += (s, e) => { if (!syncingProvider) SaveProvider(); };
            reasoning.SelectionChanged += (s, e) => { if (!syncingProvider) SaveProvider(); };

            // Composer card: what the next message shares, the prompt, then modes and send.
            regenerate = Button("Regenerate", "Ask again with the same prompt and context", () => Send(null, retry: true));
            editLast = Button("Edit prompt", "Edit and resend the last prompt, replacing its reply", () =>
            {
                prompt.Text = lastPrompt; prompt.CaretIndex = prompt.Text.Length; selected = lastTask == AiTask.Chat ? (AiTask?)null : lastTask;
                editing = true; UpdateChips(); prompt.Focus(); status.Text = "Editing the last prompt. Send replaces its reply.";
            });
            foreach (var action in new[] { regenerate, editLast }) { Small(action); action.Visibility = Visibility.Collapsed; }
            scope = Button("Whole script", "Switch between the selection and the whole script", () => { wholeScript = !wholeScript; RefreshContext(); });
            scope.Style = round; scope.Padding = new Thickness(12, 0, 12, 0); scope.Margin = new Thickness(0, 0, 6, 0); scope.FontSize = 12;
            var schemaMark = Icon("", 11); schemaMark.Margin = new Thickness(0, 1, 6, 0);
            var schemaContent = new StackPanel { Orientation = Orientation.Horizontal };
            schemaContent.Children.Add(schemaMark); schemaContent.Children.Add(new TextBlock { Text = "Schema", VerticalAlignment = VerticalAlignment.Center });
            schema.Content = schemaContent; schema.FontSize = 12;
            schema.Checked += (s, e) => schemaMark.Visibility = Visibility.Visible;
            schema.Unchecked += (s, e) => schemaMark.Visibility = Visibility.Collapsed;
            schema.Click += (s, e) => RefreshContext();
            AutomationProperties.SetName(schema, "Include schema");
            connection.SetResourceReference(Shape.FillProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var whereRow = new DockPanel { Margin = new Thickness(2, 0, 10, 0), MinHeight = 30, ToolTip = "What the next message shares: connection, editor scope and schema tables. The table count is a cached preview." };
            SetDock(connection, System.Windows.Controls.Dock.Left); whereRow.Children.Add(connection); whereRow.Children.Add(context);
            var contextRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            contextRow.Children.Add(whereRow); contextRow.Children.Add(scope); contextRow.Children.Add(schema);

            prompt.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            prompt.SetResourceReference(TextBoxBase.CaretBrushProperty, EnvironmentColors.ToolWindowTextBrushKey);
            prompt.SetResourceReference(TextBoxBase.SelectionBrushProperty, EnvironmentColors.PanelHyperlinkBrushKey);
            AutomationProperties.SetName(prompt, "AI prompt. Enter sends; Shift+Enter adds a line; Up recalls previous prompt; slash lists commands; at sign mentions a table.");
            var list = Card(suggestions, 6); list.Padding = new Thickness(4);
            list.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            suggestions.ItemContainerStyle = SuggestionStyle();
            suggest.Child = list; suggest.PlacementTarget = prompt;
            AutomationProperties.SetName(suggestions, "Suggestions. Up and Down choose, Tab or Enter inserts, Escape closes.");
            var input = new Grid { Margin = new Thickness(2, 2, 2, 0) };
            input.Children.Add(prompt); input.Children.Add(placeholder); input.Children.Add(suggest);

            var quick = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var mode in Modes)
            {
                var toggle = new ToggleButton { Content = mode.Label, Tag = mode.Task, FontSize = 12, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 6, 6, 0), MinHeight = 30, ToolTip = mode.Tip };
                toggle.Click += (s, e) => { selected = toggle.IsChecked == true ? (AiTask?)mode.Task : null; editing = false; UpdateChips(); prompt.Focus(); };
                chips.Add(toggle); quick.Children.Add(toggle);
            }
            send = new Button { Content = sendGlyph, Style = accent, Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(6, 6, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            send.Click += (s, e) =>
            {
                try { if (running != null) running.Cancel(); else Send(null); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { status.Text = Problem(error); }
            };
            var actions = new DockPanel();
            SetDock(send, System.Windows.Controls.Dock.Right); actions.Children.Add(send); actions.Children.Add(quick);
            var stack = new StackPanel();
            stack.Children.Add(contextRow); stack.Children.Add(input); stack.Children.Add(actions);
            var card = Card(stack, 10); card.Padding = new Thickness(10, 6, 8, 8);
            card.IsKeyboardFocusWithinChanged += (s, e) => card.SetResourceReference(Border.BorderBrushProperty,
                (bool)e.NewValue ? EnvironmentColors.PanelHyperlinkBrushKey : EnvironmentColors.ToolWindowBorderBrushKey);
            status.FontSize = 12; status.Opacity = 0.8; status.Margin = new Thickness(4, 6, 4, 0);
            var composer = new StackPanel { Margin = new Thickness(12, 4, 12, 10) };
            composer.Children.Add(card); composer.Children.Add(status);
            Place(composer, System.Windows.Controls.Dock.Bottom);

            scroll.Content = transcript; Children.Add(scroll);
            scroll.ScrollChanged += (s, e) => { if (e.ExtentHeightChange == 0 && e.VerticalChange != 0) followBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 24; };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                // Read-only answer boxes swallow the wheel even when they cannot scroll; only a box with its own overflow keeps it.
                for (var node = e.OriginalSource as DependencyObject; node != null && node != scroll; node = Ancestor(node))
                    if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.4); e.Handled = true;
            };
            var contextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            contextTimer.Tick += (s, e) => { if (running == null) { SyncProvider(); RefreshContext(); } };
            Loaded += (s, e) => { SyncProvider(); RefreshContext(); contextTimer.Start(); };
            Unloaded += (s, e) => { contextTimer.Stop(); running?.Cancel(); modelDiscovery?.Cancel(); suggest.IsOpen = false; };
            prompt.TextChanged += (s, e) => { placeholder.Visibility = prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateSuggestions(); };
            prompt.LostKeyboardFocus += (s, e) => suggest.IsOpen = false;
            suggestions.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (ItemsControl.ContainerFromElement(suggestions, e.OriginalSource as DependencyObject) is ListBoxItem item) { Accept(item); e.Handled = true; }
            };
            prompt.PreviewKeyDown += (s, e) =>
            {
                if (suggest.IsOpen && suggestions.Items.Count > 0)
                {
                    int count = suggestions.Items.Count;
                    if (e.Key == Key.Down || e.Key == Key.Up)
                    {
                        suggestions.SelectedIndex = (Math.Max(0, suggestions.SelectedIndex) + (e.Key == Key.Down ? 1 : count - 1)) % count;
                        suggestions.ScrollIntoView(suggestions.SelectedItem); e.Handled = true; return;
                    }
                    if (e.Key == Key.Escape) { suggest.IsOpen = false; e.Handled = true; return; }
                    if ((e.Key == Key.Tab || e.Key == Key.Enter) && Keyboard.Modifiers == ModifierKeys.None && suggestions.SelectedItem is ListBoxItem choice)
                    {
                        // Enter on a fully typed command or name sends; anything shorter completes first.
                        if (e.Key == Key.Tab || !Typed(choice)) { Accept(choice); e.Handled = true; return; }
                        suggest.IsOpen = false;
                    }
                }
                if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) { e.Handled = true; Send(null); }
                else if (e.Key == Key.Up && prompt.Text.Length == 0 && prompts.Count > 0) { e.Handled = true; prompt.Text = prompts[0]; prompt.CaretIndex = prompt.Text.Length; }
            };
            Busy(false); ShowModel(); LoadHistory(); SyncProvider(); Welcome();
        }

        private void SyncProvider()
        {
            var ai = WorkbenchPackage.Instance?.Ai;
            if (ai == null || running != null || model.IsKeyboardFocusWithin) return;
            string identity = ai.ApiFormat + ":" + AiModels.Key(ai.BaseUrl);
            syncingProvider = true;
            try
            {
                if (providerIdentity != identity)
                {
                    modelDiscovery?.Cancel(); providerIdentity = identity;
                    model.Text = AiModels.Selected(ai.BaseUrl) ?? ai.Model;
                    model.ItemsSource = AiModels.Recent(ai.BaseUrl).Concat(new[] { model.Text }).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
                    ai.Model = optionModel = model.Text;
                }
                else if (optionModel != ai.Model) model.Text = optionModel = ai.Model;
                if (optionReasoning != ai.Reasoning) reasoning.SelectedIndex = (int)(optionReasoning = ai.Reasoning);
                ShowModel();
            }
            finally { syncingProvider = false; }
        }
        private bool SaveProvider()
        {
            var ai = WorkbenchPackage.Instance?.Ai;
            if (ai == null || running != null) return ai != null;
            try
            {
                string choice = model.Text.Trim();
                if (choice.Length == 0 || choice.Length > 1024 || choice.Any(char.IsControl)) throw new InvalidOperationException("Choose or type a valid model identifier.");
                var effort = (AiReasoningLevel)Math.Max(0, reasoning.SelectedIndex);
                if (ai.Model != choice || ai.Reasoning != effort || AiModels.Selected(ai.BaseUrl) != choice)
                {
                    AiModels.Remember(ai.BaseUrl, choice); ai.Model = optionModel = choice; ai.Reasoning = optionReasoning = effort; ai.SaveSettingsToStorage();
                }
                ShowModel();
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { status.Text = "Could not save model choice: " + Problem(error); return false; }
        }
        private void LoadModels(bool refresh)
        {
            var ai = WorkbenchPackage.Instance?.Ai;
            if (ai == null || running != null || modelDiscovery != null) return;
            string url = ai.BaseUrl, identity = providerIdentity; var format = ai.ApiFormat;
            var cancellation = modelDiscovery = new CancellationTokenSource(); refreshModels.IsEnabled = false;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var choices = await AiClient.ModelsAsync(format, url, cancellation.Token, refresh);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (identity != providerIdentity || running != null) return;
                    string typed = model.Text; syncingProvider = true;
                    try { model.ItemsSource = AiModels.Recent(url).Concat(choices).Concat(new[] { typed }).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray(); model.Text = typed; }
                    finally { syncingProvider = false; }
                    status.Text = choices.Count + " provider models loaded. Type a model if yours is absent.";
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (!(error is OutOfMemoryException)) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); status.Text = Problem(error); }
                finally { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); modelDiscovery = null; cancellation.Dispose(); refreshModels.IsEnabled = running == null; }
            });
        }
        private void ShowModel()
        {
            modelLabel.Text = string.IsNullOrWhiteSpace(model.Text) ? "Choose model" : model.Text.Trim();
            string effort = ((AiReasoningLevel)Math.Max(0, reasoning.SelectedIndex)).ToString().ToLowerInvariant();
            modelButton.ToolTip = modelLabel.Text + ", reasoning " + effort + ". Change model and reasoning.";
            AutomationProperties.SetName(modelButton, "Model settings, " + modelLabel.Text);
        }
        private void ToggleDrawer()
        {
            bool open = drawer.Visibility != Visibility.Visible;
            drawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            // Collapsed pickers never build their inner toggle template, so paint them once they are laid out.
            if (open) { drawer.UpdateLayout(); ThemePicker(model); ThemePicker(reasoning); }
            chevron.Text = open ? "" : "";
            if (open) model.Focus(); else prompt.Focus();
        }

        private void Place(UIElement element, Dock side) { SetDock(element, side); Children.Add(element); }
        private static void Cell(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        private static DependencyObject Ancestor(DependencyObject node) =>
            node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        private static void ThemePicker(ComboBox picker)
        {
            picker.ApplyTemplate(); Paint(picker);
            void Paint(DependencyObject node)
            {
                // Native WPF combo templates paint their own surfaces, including the editable textbox.
                if (node is Border border) border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
                if (node is Control control)
                {
                    control.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
                    control.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                }
                if (node is System.Windows.Shapes.Path path) path.SetResourceReference(Shape.FillProperty, EnvironmentColors.ToolWindowTextBrushKey);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Paint(VisualTreeHelper.GetChild(node, i));
            }
        }

        /// <summary>Rounded, theme-following button template: surface fill, hairline border, accent border on hover and keyboard focus.</summary>
        private Style ButtonStyle(Type type, double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            border.AppendChild(content);
            var style = new Style(type, TryFindResource(type) as Style);
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(type) { VisualTree = border }));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ComboBoxBackgroundBrushKey)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
            foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
            {
                var focus = new Trigger { Property = property, Value = true };
                focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
                style.Triggers.Add(focus);
            }
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.5)); style.Triggers.Add(disabled);
            if (typeof(ToggleButton).IsAssignableFrom(type))
            {
                var chosen = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                chosen.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
                chosen.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
                chosen.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); style.Triggers.Add(chosen);
            }
            return style;
        }
        /// <summary>Borderless until hovered; used for every secondary action so the transcript stays quiet.</summary>
        private static Style Ghost(Style basedOn)
        {
            var style = new Style(typeof(Button), basedOn);
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ComboBoxBackgroundBrushKey)));
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
            style.Triggers.Add(hover); style.Triggers.Add(focus);
            return style;
        }
        /// <summary>Filled accent; the only filled control in the panel, so the send action is always findable.</summary>
        private static Style Accent(Style basedOn)
        {
            var style = new Style(typeof(Button), basedOn);
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.PanelHyperlinkBrushKey)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 0.85));
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Triggers.Add(hover); style.Triggers.Add(focus);
            return style;
        }
        private static Style SuggestionStyle()
        {
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            chrome.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            chrome.AppendChild(content);
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome }));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Setters.Add(new Setter(FocusableProperty, false));
            foreach (var property in new[] { ListBoxItem.IsSelectedProperty, IsMouseOverProperty })
            {
                var lit = new Trigger { Property = property, Value = true };
                lit.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
                style.Triggers.Add(lit);
            }
            return style;
        }

        private void RefreshContext()
        {
            if (WorkbenchPackage.Instance == null) { context.Text = "Open a query window to share its SQL."; Connected(false); return; }
            try
            {
                var preview = WorkbenchPackage.Instance.AiContextPreview(prompt.Text, wholeScript, schema.IsChecked == true);
                lastWhere = preview.Where;
                context.Text = preview.Where + (schema.IsChecked == true ? ", " + preview.Tables + (preview.Tables == 1 ? " table" : " tables") : "");
                scope.Content = preview.Scope;
                AutomationProperties.SetName(scope, preview.Scope + ". Toggle editor scope.");
                Connected(preview.Where != "No connection");
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { context.Text = "Context unavailable: " + Problem(error); }
        }
        private void Connected(bool on)
        {
            if (on == connected) return;
            connected = on;
            connection.SetResourceReference(Shape.FillProperty, on ? EnvironmentColors.PanelHyperlinkBrushKey : EnvironmentColors.ToolWindowBorderBrushKey);
        }
        private void UpdateChips() { foreach (var chip in chips) chip.IsChecked = selected == (AiTask)chip.Tag; }
        private void Busy(bool on)
        {
            fresh.IsEnabled = export.IsEnabled = scope.IsEnabled = schema.IsEnabled = regenerate.IsEnabled = editLast.IsEnabled = !on;
            model.IsEnabled = reasoning.IsEnabled = !on; refreshModels.IsEnabled = !on && modelDiscovery == null;
            foreach (var chip in chips) chip.IsEnabled = !on;
            sendGlyph.Text = on ? "" : "";
            AutomationProperties.SetName(send, on ? "Stop" : "Send");
            send.ToolTip = on ? "Stop and keep the partial answer" : "Send (Enter). Shift+Enter adds a line.";
            if (on) suggest.IsOpen = false;
        }

        private static TextBlock Icon(string glyph, double size = 14) =>
            new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        private static Border Card(UIElement child, double radius)
        {
            var card = new Border { Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius) };
            card.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return card;
        }
        private Button Button(string text, string tip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tip, Style = ghost, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0), MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, text);
            button.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { status.Text = Problem(error); }
            };
            return button;
        }
        private Button IconButton(string glyph, string name, string tip, Action action)
        {
            var button = Button(name, tip, action);
            button.Content = Icon(glyph); button.Width = 30; button.Height = 30; button.Padding = new Thickness(0); button.Margin = new Thickness(0);
            return button;
        }
        private static Button Small(Button button) { button.FontSize = 12; button.Padding = new Thickness(8, 0, 8, 0); button.Margin = new Thickness(0, 0, 2, 0); return button; }

        private void Welcome()
        {
            var welcome = new StackPanel { Margin = new Thickness(0, 24, 0, 12), Tag = "welcome" };
            welcome.Children.Add(new TextBlock { Text = "Ask about the SQL in front of you", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            welcome.Children.Add(new TextBlock { Text = "Querywright reads the open query, your selection and the connected database's schema. Review generated SQL before you run it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), FontSize = 13, LineHeight = 20, Opacity = 0.8 });
            foreach (var (glyph, label, text) in new[]
            {
                ("", "Write a query from a description", "/write "), ("", "Explain the selection", "/explain "),
                ("", "Review for problems", "/review "), ("", "Mention a table with @", "@"), ("", "See every command", "/")
            })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = Icon(glyph); icon.Width = 18; icon.Margin = new Thickness(0, 0, 10, 0); icon.Opacity = 0.7;
                row.Children.Add(icon); row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                var button = Button(label, "Start the prompt with " + text.Trim(), () => { prompt.Text = text; prompt.CaretIndex = prompt.Text.Length; prompt.Focus(); UpdateSuggestions(); });
                button.Content = row; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(8, 0, 8, 0); button.MinHeight = 34; button.Margin = new Thickness(-8, 0, 0, 0);
                welcome.Children.Add(button);
            }
            transcript.Children.Add(welcome);
        }

        private StackPanel Message(string role, string text)
        {
            if (transcript.Children.Count == 1 && (transcript.Children[0] as FrameworkElement)?.Tag as string == "welcome") transcript.Children.Clear();
            var turn = new Turn { Role = role, Text = text };
            var message = new StackPanel { Tag = turn };
            var body = new StackPanel();
            if (role == "You")
            {
                message.Margin = new Thickness(48, 10, 0, 6);
                body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 });
                var bubble = Card(body, 0); bubble.CornerRadius = new CornerRadius(12, 12, 4, 12); bubble.Padding = new Thickness(12, 7, 12, 8); bubble.HorizontalAlignment = HorizontalAlignment.Right;
                turn.Caption = new TextBlock { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 4, 0), Visibility = Visibility.Collapsed };
                message.Children.Add(bubble); message.Children.Add(turn.Caption);
            }
            else
            {
                message.Margin = new Thickness(0, 10, 0, 10);
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var mark = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                mark.SetResourceReference(Shape.FillProperty, EnvironmentColors.PanelHyperlinkBrushKey);
                turn.Mark = mark; head.Children.Add(mark);
                head.Children.Add(new TextBlock { Text = "Querywright", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (!string.IsNullOrWhiteSpace(model.Text)) head.Children.Add(new TextBlock { Text = model.Text.Trim(), FontSize = 12, Opacity = 0.65, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                message.Children.Add(head); message.Children.Add(body); Render(body, text);
            }
            transcript.Children.Add(message); KeepBottom();
            return body;
        }

        private void KeepBottom() { if (followBottom) { scroll.UpdateLayout(); scroll.ScrollToEnd(); } }

        private static RichTextBox Readable(FlowDocument document, double size = 14, double line = 22)
        {
            document.PagePadding = new Thickness(0); document.FontSize = size; document.LineHeight = line;
            var box = new RichTextBox { Document = document, IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent };
            box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, EnvironmentColors.PanelHyperlinkBrushKey);
            return box;
        }

        private void Render(StackPanel body, string text)
        {
            body.Children.Clear();
            var document = new FlowDocument();
            System.Windows.Documents.List list = null;
            foreach (var block in AiMarkdown.Parse(text))
            {
                if (block.Kind != AiBlockKind.ListItem) list = null;
                if (block.Kind == AiBlockKind.Code) { document.Blocks.Add(new BlockUIContainer(CodeBlock(block)) { Margin = new Thickness(0, 4, 0, 14) }); continue; }
                if (block.Kind == AiBlockKind.Table) { document.Blocks.Add(TableBlock(block)); continue; }
                var prose = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                AddInlines(prose.Inlines, block.Inlines);
                if (block.Kind == AiBlockKind.Heading)
                {
                    prose.FontSize = Math.Max(15, 22 - block.Level * 2); prose.FontWeight = FontWeights.SemiBold; prose.Margin = new Thickness(0, 6, 0, 6);
                }
                if (block.Kind == AiBlockKind.ListItem)
                {
                    bool numbered = char.IsDigit(block.Prefix[0]);
                    if (list == null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                    {
                        list = new System.Windows.Documents.List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0) };
                        if (numbered && int.TryParse(block.Prefix.TrimEnd('.', ')'), out int start) && start > 0) list.StartIndex = start;
                        document.Blocks.Add(list);
                    }
                    prose.Margin = new Thickness(0, 0, 0, 4);
                    list.ListItems.Add(new System.Windows.Documents.ListItem(prose));
                    continue;
                }
                document.Blocks.Add(prose);
            }
            body.Children.Add(Readable(document)); KeepBottom();
        }

        /// <summary>The panel's one bold element: SQL on a surface with an accent rail, actions in its header.</summary>
        private Border CodeBlock(AiBlock block)
        {
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Small(Button("Copy", "Copy this code block", () => { Clipboard.SetText(block.Text); status.Text = "Code copied."; })));
            if (block.IsSql && block.Closed && !string.IsNullOrWhiteSpace(block.Text))
            {
                actions.Children.Add(SqlButton("Insert", "Insert at the cursor in the query window", () => WorkbenchPackage.Instance.AiInsertAsync(block.Text, false)));
                actions.Children.Add(SqlButton("Replace", "Replace the selection in the query window", () => WorkbenchPackage.Instance.AiInsertAsync(block.Text, true)));
                actions.Children.Add(SqlButton("New window", "Open in a new query window", () => WorkbenchPackage.Instance.AiOpenAsync(block.Text, false)));
                actions.Children.Add(SqlButton("Run", "Open in a new query window and run it", () => WorkbenchPackage.Instance.AiOpenAsync(block.Text, true)));
            }
            var head = new DockPanel { Margin = new Thickness(12, 3, 4, 0) };
            var language = new TextBlock { Text = block.Language.Length > 0 ? block.Language : "SQL", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            SetDock(language, System.Windows.Controls.Dock.Left); head.Children.Add(language); head.Children.Add(actions);
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
            var code = Readable(new FlowDocument(paragraph) { FontFamily = CodeFont }, 13, 19); code.Margin = new Thickness(12, 2, 12, 10);
            var stack = new StackPanel(); stack.Children.Add(head); stack.Children.Add(code);
            var rail = new Border { Width = 3, CornerRadius = new CornerRadius(5, 0, 0, 5) };
            rail.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.PanelHyperlinkBrushKey);
            var dock = new DockPanel(); SetDock(rail, System.Windows.Controls.Dock.Left); dock.Children.Add(rail); dock.Children.Add(stack);
            return Card(dock, 6);
        }

        private static Table TableBlock(AiBlock block)
        {
            var rows = AiMarkdown.TableRows(block.Text);
            var table = new Table { CellSpacing = 0, FontSize = 13, Margin = new Thickness(0, 2, 0, 12) };
            for (int i = 0; i < (rows.Count == 0 ? 0 : rows[0].Count); i++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var group = new TableRowGroup(); table.RowGroups.Add(group);
            for (int r = 0; r < rows.Count; r++)
            {
                var row = new TableRow();
                foreach (string text in rows[r])
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
                    AddInlines(paragraph.Inlines, AiMarkdown.Inlines(text));
                    if (r == 0) paragraph.FontWeight = FontWeights.SemiBold;
                    var cell = new TableCell(paragraph) { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
                    cell.SetResourceReference(TableCell.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                    if (r == 0) cell.SetResourceReference(TableCell.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
                    row.Cells.Add(cell);
                }
                group.Rows.Add(row);
            }
            return table;
        }

        private static void AddInlines(InlineCollection target, IEnumerable<AiInline> items)
        {
            foreach (var item in items)
            {
                var run = new Run(item.Text);
                if (item.Bold) run.FontWeight = FontWeights.SemiBold;
                if (item.Italic) run.FontStyle = FontStyles.Italic;
                if (item.Code) { run.FontFamily = CodeFont; run.FontSize = 13; run.SetResourceReference(TextElement.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey); }
                target.Add(run);
            }
        }

        private Button SqlButton(string text, string tip, Func<Task> action) => Small(Button(text, tip, () =>
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); status.Text = Problem(error); }
            })));

        internal void FocusPrompt() => prompt.Focus();
        internal void Start(AiTask task) { selected = task; UpdateChips(); Send(task); }

        private void NewConversation()
        {
            conversation.Clear(); transcript.Children.Clear(); status.Text = ""; prompt.Clear(); selected = null;
            lastRequest = null; lastBody = null; lastUser = null; beforeLast = null; editing = false;
            regenerate.Visibility = editLast.Visibility = Visibility.Collapsed;
            followBottom = true; UpdateChips(); Welcome(); prompt.Focus(); RefreshContext();
        }

        private void Help()
        {
            // Local reference card: never sent to the provider and left out of exports.
            var body = Message("Assistant", AiAssist.SlashHelp);
            if (body.Parent is FrameworkElement message) message.Tag = null;
        }

        private void Export()
        {
            var turns = transcript.Children.OfType<FrameworkElement>().Select(e => e.Tag).OfType<Turn>()
                .Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => (t.Role, t.Text)).ToList();
            if (turns.Count == 0) { status.Text = "Nothing to export yet. Ask something first."; return; }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export conversation", Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt", DefaultExt = ".md", AddExtension = true,
                FileName = "Querywright AI " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".md"
            };
            var owner = Window.GetWindow(this);
            if ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
            File.WriteAllText(dialog.FileName, AiAssist.Transcript(turns, lastWhere, DateTime.Now));
            status.Text = "Conversation exported to " + Path.GetFileName(dialog.FileName) + ".";
        }

        private void UpdateSuggestions()
        {
            suggestions.Items.Clear();
            if (running == null && prompt.IsKeyboardFocusWithin)
            {
                string text = prompt.Text; int caret = prompt.CaretIndex;
                var slash = Regex.Match(text.Substring(0, caret), @"^\s*/([A-Za-z-]*)$");
                if (slash.Success)
                {
                    foreach (var command in Commands.Where(c => c.Name.StartsWith(slash.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                        suggestions.Items.Add(Suggestion("/" + command.Name, command.Detail, slash.Groups[1].Index - 1, "/" + command.Name + " "));
                }
                else
                {
                    var mention = AiAssist.MentionAt(text, caret);
                    if (mention != null && WorkbenchPackage.Instance != null)
                        foreach (var name in WorkbenchPackage.Instance.AiMentionNames(mention.Value.Typed))
                            suggestions.Items.Add(Suggestion(name.Name, name.IsView ? "view" : "table", mention.Value.Start, "@" + Quote(name.Name) + " "));
                }
            }
            suggest.IsOpen = suggestions.Items.Count > 0;
            if (suggest.IsOpen) suggestions.SelectedIndex = 0;
        }
        private static ListBoxItem Suggestion(string label, string detail, int start, string insert)
        {
            var row = new DockPanel();
            var hint = new TextBlock { Text = detail, FontSize = 12, Opacity = 0.65, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            SetDock(hint, System.Windows.Controls.Dock.Right); row.Children.Add(hint);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new ListBoxItem { Content = row, Tag = (start, insert) };
            AutomationProperties.SetName(item, label + ", " + detail);
            return item;
        }
        private static string Quote(string name) => string.Join(".", name.Split(new[] { '.' }, 2).Select(part => Regex.IsMatch(part, @"^[A-Za-z_#][\w#$]*$") ? part : "[" + part + "]"));
        private bool Typed(ListBoxItem item)
        {
            var (start, insert) = ((int, string))item.Tag;
            int caret = prompt.CaretIndex;
            return caret >= start && string.Equals(prompt.Text.Substring(start, caret - start), insert.TrimEnd(), StringComparison.OrdinalIgnoreCase);
        }
        private void Accept(ListBoxItem item)
        {
            var (start, insert) = ((int, string))item.Tag;
            string text = prompt.Text; int caret = Math.Max(start, Math.Min(prompt.CaretIndex, text.Length));
            prompt.Text = text.Substring(0, start) + insert + text.Substring(caret);
            prompt.CaretIndex = start + insert.Length; suggest.IsOpen = false; prompt.Focus();
        }

        private static void Pulse(FrameworkElement mark, bool on)
        {
            if (mark == null) return;
            if (!on || !SystemParameters.ClientAreaAnimation) { mark.BeginAnimation(OpacityProperty, null); return; }
            mark.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

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
            if (running != null) { status.Text = "Wait for this answer, or stop it first."; return; }
            if (retry && lastRequest == null) return;
            suggest.IsOpen = false;
            string raw = prompt.Text.Trim(), typed = raw;
            if (!retry && forced == null)
            {
                var command = AiAssist.SlashCommand(raw);
                if (command.Action == "clear") { NewConversation(); return; }
                if (command.Action == "export") { prompt.Clear(); Export(); return; }
                if (command.Action == "help") { prompt.Clear(); Help(); return; }
                if (command.Task != null) { forced = command.Task; typed = command.Prompt.Trim(); }
            }
            SyncProvider();
            if (!SaveProvider()) return;
            string usedModel = model.Text.Trim(); var usedReasoning = (AiReasoningLevel)Math.Max(0, reasoning.SelectedIndex);
            var task = retry ? lastTask : forced ?? selected ?? (conversation.Count == 0 ? AiTask.Generate : AiTask.Chat);
            string instruction = retry ? lastPrompt : typed;
            if ((task == AiTask.Generate || task == AiTask.Chat || task == AiTask.Edit) && instruction.Length == 0) { status.Text = "Type a prompt first."; prompt.Focus(); return; }
            var cancellation = running = new CancellationTokenSource();
            StackPanel body;
            if (retry) body = lastBody;
            else
            {
                if (editing && lastBody != null)
                {
                    transcript.Children.Remove(lastUser); transcript.Children.Remove((UIElement)lastBody.Parent);
                    conversation.Clear(); conversation.AddRange(beforeLast);
                }
                beforeLast = conversation.ToList(); editing = false;
                Message("You", instruction.Length > 0 ? instruction : Modes.Where(m => m.Task == task).Select(m => m.Label).DefaultIfEmpty(task.ToString()).First());
                lastUser = transcript.Children[transcript.Children.Count - 1];
                body = lastBody = Message("Assistant", "");
                lastRequest = null; lastPrompt = instruction; lastTask = task;
                // The prompt stays editable while the answer streams, so the next question can be drafted.
                if (raw.Length > 0) Remember(raw);
                prompt.Clear();
            }
            var turn = (body.Parent as FrameworkElement)?.Tag as Turn;
            var asked = (lastUser as FrameworkElement)?.Tag as Turn;
            var thinking = new TextBlock { Text = "Thinking…", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetLiveSetting(thinking, AutomationLiveSetting.Polite);
            body.Children.Clear(); body.Children.Add(thinking); Pulse(turn?.Mark, true); status.Text = "";
            modelDiscovery?.Cancel();
            var deltas = new DeltaBuffer(); var thoughts = new DeltaBuffer();
            var live = new Paragraph { Margin = new Thickness(0) };
            var liveBox = Readable(new FlowDocument(live));
            var thoughtParagraph = new Paragraph { Margin = new Thickness(0) };
            var thoughtBox = Readable(new FlowDocument(thoughtParagraph), 13, 20); thoughtBox.MaxHeight = 300; thoughtBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; thoughtBox.Opacity = 0.85;
            var thoughtHeader = new TextBlock { Text = "Reasoning" }; thoughtHeader.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var thoughtView = new Expander { Header = thoughtHeader, Content = thoughtBox, IsExpanded = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
            thoughtView.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AutomationProperties.SetName(thoughtView, "Provider reasoning. Read-only, separate from answer.");
            var reasoningClock = System.Diagnostics.Stopwatch.StartNew();
            bool started = false;
            void StartLive()
            {
                if (started) return;
                body.Children.Clear(); body.Children.Add(thoughtView); body.Children.Add(liveBox); started = true;
            }
            void Flush()
            {
                string thought = thoughts.Take();
                if (thought.Length > 0)
                {
                    StartLive(); thoughtView.Visibility = Visibility.Visible;
                    thoughtParagraph.Inlines.Add(new Run(thought));
                }
                if (thoughtView.Visibility == Visibility.Visible) thoughtHeader.Text = "Reasoning, " + reasoningClock.Elapsed.TotalSeconds.ToString("0.0") + " s";
                string delta = deltas.Take();
                if (delta.Length > 0)
                {
                    StartLive(); reasoningClock.Stop(); live.Inlines.Add(new Run(delta));
                }
                if (thought.Length > 0 || delta.Length > 0) KeepBottom();
            }
            // ponytail: append plain text during streaming, format once on completion to avoid markdown/control churn.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (s, e) => Flush(); timer.Start();
            Busy(true);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    AiReply reply = retry
                        ? await WorkbenchPackage.Instance.AiReplyAsync(lastRequest, conversation, cancellation.Token, deltas, thoughts)
                        : await WorkbenchPackage.Instance.AiAskAsync(task, instruction, conversation, text => thinking.Text = text, cancellation.Token, deltas, wholeScript, schema.IsChecked == true,
                            (messages, description) =>
                            {
                                lastRequest = messages.ToList(); lastContext = description;
                                if (asked?.Caption != null) { asked.Caption.Text = "Context: " + description; asked.Caption.Visibility = Visibility.Visible; }
                            }, thoughts);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    timer.Stop(); Flush(); reasoningClock.Stop();
                    regenerate.Content = "Regenerate"; AutomationProperties.SetName(regenerate, "Regenerate");
                    Finish(body, reply.Text); AddReasoning(body, reply.Reasoning, reasoningClock.Elapsed.TotalSeconds);
                    var details = new List<string> { usedModel, "reasoning " + usedReasoning.ToString().ToLowerInvariant() };
                    if (reply.ReasoningUnsupported) details.Add("reasoning not supported by this model");
                    if (reply.InputTokens.HasValue) details.Add(reply.InputTokens.Value.ToString("N0") + " tokens in");
                    if (reply.OutputTokens.HasValue) details.Add(reply.OutputTokens.Value.ToString("N0") + " tokens out");
                    if (reply.ReasoningTokens.HasValue) details.Add(reply.ReasoningTokens.Value.ToString("N0") + " reasoning tokens");
                    if (reply.TokenLimitReached) details.Add("output limit reached, so the answer may be incomplete");
                    status.Text = string.Join(", ", details) + ". Review SQL before you run it.";
                    if (!retry) { selected = null; UpdateChips(); }
                }
                catch (OperationCanceledException)
                {
                    Diagnostics.Write("ai_panel", "stopped", "canceled");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); timer.Stop();
                    reasoningClock.Stop(); Finish(body, deltas.Text); AddReasoning(body, thoughts.Text, reasoningClock.Elapsed.TotalSeconds);
                    Note(body, deltas.Text.Length > 0 ? "Stopped. The answer above is partial." : "Stopped before an answer arrived.", null); status.Text = "Stopped.";
                    if (!retry && deltas.Text.Length == 0) Restore();
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    Diagnostics.Write("ai_panel", "failed", "error", error: error);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); timer.Stop();
                    regenerate.Content = "Retry"; AutomationProperties.SetName(regenerate, "Retry");
                    reasoningClock.Stop(); Finish(body, deltas.Text); AddReasoning(body, thoughts.Text, reasoningClock.Elapsed.TotalSeconds);
                    Note(body, Problem(error), ""); status.Text = Problem(error);
                    if (!retry && deltas.Text.Length == 0) Restore();
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    timer.Stop(); Pulse(turn?.Mark, false);
                    running = null; cancellation.Dispose();
                    Busy(false); schema.IsChecked = true;
                    regenerate.IsEnabled = editLast.IsEnabled = lastRequest != null;
                    regenerate.Visibility = editLast.Visibility = lastRequest != null ? Visibility.Visible : Visibility.Collapsed;
                    RefreshContext();
                }
            });
            void Restore()
            {
                // Nothing came back: give the prompt back, and let the next send replace this empty turn.
                if (prompt.Text.Length > 0) return;
                prompt.Text = raw; prompt.CaretIndex = prompt.Text.Length; editing = true;
            }
        }

        private void Finish(StackPanel body, string reply)
        {
            Render(body, reply);
            if ((body.Parent as FrameworkElement)?.Tag is Turn turn) turn.Text = reply;
            var footer = new WrapPanel { Tag = "footer", Margin = new Thickness(-8, 0, 0, 0) };
            if (reply.Length > 0) footer.Children.Add(Small(Button("Copy answer", "Copy the whole answer as Markdown", () => { Clipboard.SetText(reply); status.Text = "Answer copied."; })));
            foreach (var action in new[] { regenerate, editLast })
            {
                (action.Parent as Panel)?.Children.Remove(action);
                action.Visibility = lastRequest != null ? Visibility.Visible : Visibility.Collapsed;
                footer.Children.Add(action);
            }
            body.Children.Add(footer);
            KeepBottom();
        }

        private static void Note(StackPanel body, string text, string glyph)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (glyph != null) { var icon = Icon(glyph, 13); icon.Margin = new Thickness(0, 2, 8, 0); icon.VerticalAlignment = VerticalAlignment.Top; SetDock(icon, System.Windows.Controls.Dock.Left); row.Children.Add(icon); }
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontStyle = glyph == null ? FontStyles.Italic : FontStyles.Normal });
            int last = body.Children.Count - 1;
            body.Children.Insert(last >= 0 && (body.Children[last] as FrameworkElement)?.Tag as string == "footer" ? last : body.Children.Count, row);
        }

        private void AddReasoning(StackPanel body, string text, double seconds)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var document = new FlowDocument(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
            var content = Readable(document, 13, 20); content.MaxHeight = 300; content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; content.Opacity = 0.85;
            var header = new TextBlock { Text = "Reasoning, " + seconds.ToString("0.0") + " s" }; header.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var expander = new Expander { Header = header, Content = content, IsExpanded = false, Margin = new Thickness(0, 0, 0, 8) };
            expander.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AutomationProperties.SetName(expander, "Provider reasoning. Read-only, separate from answer.");
            body.Children.Insert(0, expander);
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
