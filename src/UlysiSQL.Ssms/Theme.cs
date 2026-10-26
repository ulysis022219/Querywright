using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UlysiSQL.Ssms
{
    /// <summary>Window colors: follow the SSMS color theme or use a fixed palette.</summary>
    [TypeConverter(typeof(FriendlyEnumConverter))]
    public enum UiTheme
    {
        [Description("Follow SSMS")] FollowSsms,
        Light,
        Dark,
        Midnight,
        [Description("Warm light")] WarmLight,
    }

    /// <summary>Highlight color for primary buttons, chips, focus rings and SQL keywords.</summary>
    [TypeConverter(typeof(FriendlyEnumConverter))]
    public enum UiAccent
    {
        [Description("Follow SSMS")] FollowSsms,
        Blue,
        Teal,
        Violet,
        Rose,
        Amber,
        Green,
        Graphite,
    }

    /// <summary>Shows enum descriptions in Tools &gt; Options and stores them; also reads plain member names.</summary>
    public sealed class FriendlyEnumConverter : EnumConverter
    {
        public FriendlyEnumConverter(Type type) : base(type) { }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value != null && value.GetType() == EnumType)
            {
                var field = EnumType.GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static);
                if (field?.GetCustomAttribute<DescriptionAttribute>() is DescriptionAttribute description) return description.Description;
            }
            return base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string text)
                foreach (var field in EnumType.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (string.Equals(field.GetCustomAttribute<DescriptionAttribute>()?.Description, text.Trim(), StringComparison.OrdinalIgnoreCase))
                        return field.GetValue(null);
            return base.ConvertFrom(context, culture, value);
        }
    }

    /// <summary>Brush keys for one host: SSMS environment colors, or system colors when running standalone.</summary>
    internal sealed class ThemeKeys
    {
        internal object Back { get; private set; }
        internal object Text { get; private set; }
        internal object Surface { get; private set; }
        internal object Border { get; private set; }
        internal object Accent { get; private set; }
        internal bool IsHost { get; private set; }

        internal static readonly ThemeKeys Standalone = new ThemeKeys
        {
            Back = SystemColors.ControlBrushKey, Text = SystemColors.ControlTextBrushKey, Surface = SystemColors.WindowBrushKey,
            Border = SystemColors.ControlDarkBrushKey, Accent = SystemColors.HighlightBrushKey,
        };

        private static ThemeKeys host;
        /// <summary>SSMS keys. Reading this loads the SSMS SDK assemblies.</summary>
        internal static ThemeKeys Host => host ?? (host = CreateHost());
        /// <summary>Host keys inside SSMS; system keys for standalone dialog checks.</summary>
        internal static ThemeKeys ForWindow => Application.Current != null ? Host : Standalone;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ThemeKeys CreateHost() => new ThemeKeys
        {
            IsHost = true,
            Back = Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey,
            Text = Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey,
            Surface = Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ComboBoxBackgroundBrushKey,
            Border = Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBorderBrushKey,
            Accent = Microsoft.VisualStudio.PlatformUI.EnvironmentColors.PanelHyperlinkBrushKey,
        };
    }

    /// <summary>
    /// Palettes, accent colors and shared control templates. Following SSMS keeps every base color on the host's
    /// dynamic resource keys, so SSMS theme switches flow through; a fixed palette overrides those keys per window.
    /// </summary>
    internal static class Theme
    {
        internal static UiTheme Mode { get; private set; }
        internal static UiAccent Accent { get; private set; }
        /// <summary>Raised on the UI thread when the palette option or the SSMS color theme changes.</summary>
        internal static event EventHandler Changed;

        /// <summary>Text drawn on an accent fill.</summary>
        internal static readonly ComponentResourceKey OnAccentKey = new ComponentResourceKey(typeof(Theme), "OnAccent");
        /// <summary>Translucent accent for hover, checked chips and badges.</summary>
        internal static readonly ComponentResourceKey AccentSoftKey = new ComponentResourceKey(typeof(Theme), "AccentSoft");
        /// <summary>Secondary text: descriptions, hints, empty states.</summary>
        internal static readonly ComponentResourceKey MutedKey = new ComponentResourceKey(typeof(Theme), "Muted");

        /// <summary>Leading icon drawn by the shared button template; content stays plain text for access keys and automation.</summary>
        internal static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Theme));
        internal static void SetIcon(DependencyObject element, Geometry value) => element.SetValue(IconProperty, value);
        internal static Geometry GetIcon(DependencyObject element) => (Geometry)element.GetValue(IconProperty);

        /// <summary>Fills a button with the accent color without making it the default button.</summary>
        internal static readonly DependencyProperty PrimaryProperty = DependencyProperty.RegisterAttached("Primary", typeof(bool), typeof(Theme));
        internal static void SetPrimary(DependencyObject element, bool value) => element.SetValue(PrimaryProperty, value);

        /// <summary>True when a fixed palette replaces the SSMS colors.</summary>
        internal static bool Overridden => Fixed(Mode) != null && !SystemParameters.HighContrast;

        internal static void Configure(UiTheme mode, UiAccent accent)
        {
            if (!Enum.IsDefined(typeof(UiTheme), mode)) mode = UiTheme.FollowSsms;
            if (!Enum.IsDefined(typeof(UiAccent), accent)) accent = UiAccent.FollowSsms;
            if (mode == Mode && accent == Accent) return;
            Mode = mode; Accent = accent;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static bool hooked;
        /// <summary>Re-applies open windows when SSMS switches between its light, dark and blue themes.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void HookHost()
        {
            if (hooked) return;
            hooked = true;
            Microsoft.VisualStudio.PlatformUI.VSColorTheme.ThemeChanged += e => Changed?.Invoke(null, EventArgs.Empty);
        }

        private sealed class Palette
        {
            internal Palette(int back, int text, int surface, int border) { Back = C(back); Text = C(text); Surface = C(surface); Border = C(border); }
            internal Color Back { get; }
            internal Color Text { get; }
            internal Color Surface { get; }
            internal Color Border { get; }
        }

        private static readonly Palette LightPalette = new Palette(0xF6F7F9, 0x1F2328, 0xFFFFFF, 0xD0D7DE);
        private static readonly Palette DarkPalette = new Palette(0x1F1F1F, 0xE6E6E6, 0x2B2B2B, 0x484848);
        private static readonly Palette MidnightPalette = new Palette(0x0F172A, 0xE2E8F0, 0x1E293B, 0x334155);
        private static readonly Palette WarmPalette = new Palette(0xFAF6EF, 0x3B3127, 0xFFFDF9, 0xE0D5C3);

        private static Palette Fixed(UiTheme mode) => mode switch
        {
            UiTheme.Light => LightPalette,
            UiTheme.Dark => DarkPalette,
            UiTheme.Midnight => MidnightPalette,
            UiTheme.WarmLight => WarmPalette,
            _ => null,
        };

        // Light variants keep 4.5:1 on white; dark variants keep it on the dark palettes.
        private static Color AccentColor(UiAccent accent, bool dark) => accent switch
        {
            UiAccent.Teal => C(dark ? 0x2DD4BF : 0x0F766E),
            UiAccent.Violet => C(dark ? 0xA78BFA : 0x6D28D9),
            UiAccent.Rose => C(dark ? 0xFB7185 : 0xBE123C),
            UiAccent.Amber => C(dark ? 0xFBBF24 : 0xB45309),
            UiAccent.Green => C(dark ? 0x4ADE80 : 0x15803D),
            UiAccent.Graphite => C(dark ? 0xCBD5E1 : 0x475569),
            _ => C(dark ? 0x60A5FA : 0x1D4ED8),
        };

        private static readonly object OwnedKey = new object();
        private static readonly object HostStylesKey = new object();

        /// <summary>Puts the chosen palette into <paramref name="root"/>'s resources, replacing an earlier call's.</summary>
        internal static void Apply(FrameworkElement root, ThemeKeys keys)
        {
            var resources = root.Resources;
            if (resources[OwnedKey] is List<object> earlier) foreach (var key in earlier) resources.Remove(key);
            var owned = new List<object>();
            resources[OwnedKey] = owned;
            void Put(object key, Color color) { resources[key] = Frozen(color); owned.Add(key); }

            bool contrast = SystemParameters.HighContrast;
            var palette = contrast ? null : Fixed(Mode);
            if (keys.IsHost) HostStyles(root, merge: palette == null);
            Color back = palette?.Back ?? Lookup(root, keys.Back, SystemColors.ControlColor);
            Color text = palette?.Text ?? Lookup(root, keys.Text, SystemColors.ControlTextColor);
            bool dark = Luminance(back) < 0.35;
            bool ownAccent = !contrast && (Accent != UiAccent.FollowSsms || palette != null);
            Color accent = ownAccent ? AccentColor(Accent, dark) : Lookup(root, keys.Accent, SystemColors.HighlightColor);
            Color onAccent = contrast ? SystemColors.HighlightTextColor : ReadableOn(accent);
            if (palette != null)
            {
                Put(keys.Back, palette.Back); Put(keys.Text, palette.Text); Put(keys.Surface, palette.Surface); Put(keys.Border, palette.Border);
                // Native WPF templates read system colors.
                Put(SystemColors.WindowBrushKey, palette.Surface); Put(SystemColors.WindowTextBrushKey, palette.Text);
                Put(SystemColors.ControlBrushKey, palette.Back); Put(SystemColors.ControlTextBrushKey, palette.Text);
                Put(SystemColors.ControlDarkBrushKey, palette.Border); Put(SystemColors.ActiveBorderBrushKey, palette.Border);
                Put(SystemColors.GrayTextBrushKey, Mix(text, back, 0.6)); Put(SystemColors.HighlightTextBrushKey, onAccent);
                // Without the 4.5 compatibility switch these keys alias ControlBrushKey and ControlTextBrushKey.
                if (!Equals(SystemColors.InactiveSelectionHighlightBrushKey, SystemColors.ControlBrushKey))
                {
                    Put(SystemColors.InactiveSelectionHighlightBrushKey, Mix(accent, back, 0.3)); Put(SystemColors.InactiveSelectionHighlightTextBrushKey, text);
                }
                if (!Equals(keys.Accent, SystemColors.HighlightBrushKey)) Put(SystemColors.HighlightBrushKey, accent);
            }
            if (ownAccent) Put(keys.Accent, accent);
            Put(OnAccentKey, onAccent);
            Put(AccentSoftKey, Color.FromArgb(dark ? (byte)0x50 : (byte)0x2A, accent.R, accent.G, accent.B));
            Put(MutedKey, contrast ? text : Mix(text, back, 0.66));
        }

        // Fixed palettes drop the host dialog styles, whose own color keys would otherwise keep the SSMS colors.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HostStyles(FrameworkElement root, bool merge)
        {
            var current = root.Resources[HostStylesKey] as ResourceDictionary;
            if (merge == (current != null)) return;
            if (current != null) { root.Resources.MergedDictionaries.Remove(current); root.Resources.Remove(HostStylesKey); }
            else if (root.TryFindResource(Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary styles)
            {
                root.Resources.MergedDictionaries.Insert(0, styles);
                root.Resources[HostStylesKey] = styles;
            }
        }

        /// <summary>WinForms colors for the current palette, or null to keep the native look.</summary>
        internal static (System.Drawing.Color Back, System.Drawing.Color Text, System.Drawing.Color Surface, System.Drawing.Color Border, System.Drawing.Color Accent, System.Drawing.Color OnAccent)? FormColors(ThemeKeys keys)
        {
            if (SystemParameters.HighContrast || (!keys.IsHost && !Overridden)) return null;
            var probe = new Border();
            Apply(probe, keys);
            Color Get(object key) => probe.Resources[key] is SolidColorBrush own ? own.Color
                : Application.Current?.TryFindResource(key) is SolidColorBrush host ? host.Color : SystemColors.ControlColor;
            System.Drawing.Color D(Color c) => System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            if (keys.IsHost && !(Application.Current?.TryFindResource(keys.Back) is SolidColorBrush) && !Overridden) return null;
            return (D(Get(keys.Back)), D(Get(keys.Text)), D(Get(keys.Surface)), D(Get(keys.Border)), D(Get(keys.Accent)), D(Get(OnAccentKey)));
        }

        /// <summary>Rounded button with an optional leading <see cref="IconProperty"/>; default and primary buttons fill with the accent.</summary>
        internal static Style ButtonStyle(Type type, ThemeKeys keys, Style basedOn, double radius = 6)
        {
            var style = new Style(type, basedOn);
            // High contrast keeps the native templates and system colors.
            if (SystemParameters.HighContrast) return style;
            var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var grid = new FrameworkElementFactory(typeof(Grid));
            var hover = new FrameworkElementFactory(typeof(Border), "hover");
            hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Max(0, radius - 1)));
            hover.SetResourceReference(Border.BackgroundProperty, AccentSoftKey);
            hover.SetValue(UIElement.OpacityProperty, 0.0);
            grid.AppendChild(hover);
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            row.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var icon = new FrameworkElementFactory(typeof(Viewbox), "icon");
            icon.SetValue(FrameworkElement.WidthProperty, 16.0);
            icon.SetValue(FrameworkElement.HeightProperty, 16.0);
            icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var canvas = new FrameworkElementFactory(typeof(Canvas));
            canvas.SetValue(FrameworkElement.WidthProperty, Icons.Size);
            canvas.SetValue(FrameworkElement.HeightProperty, Icons.Size);
            var path = new FrameworkElementFactory(typeof(Path));
            path.SetValue(Path.DataProperty, new TemplateBindingExtension(IconProperty));
            path.SetValue(Shape.FillProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            canvas.AppendChild(path); icon.AppendChild(canvas); row.AppendChild(icon);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(content); grid.AppendChild(row);
            var ring = new FrameworkElementFactory(typeof(Border), "ring");
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-1));
            ring.SetResourceReference(Border.BorderBrushProperty, keys.Accent);
            ring.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            grid.AppendChild(ring);
            chrome.AppendChild(grid);

            var template = new ControlTemplate(type) { VisualTree = chrome };
            template.Triggers.Add(When(IconProperty, null, new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "icon")));
            template.Triggers.Add(When(UIElement.IsMouseOverProperty, true, new Setter(UIElement.OpacityProperty, 1.0, "hover")));
            template.Triggers.Add(When(ButtonBase.IsPressedProperty, true, new Setter(UIElement.OpacityProperty, 0.85, "chrome")));
            template.Triggers.Add(When(UIElement.IsKeyboardFocusedProperty, true, new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ring")));
            var primaryHover = new MultiTrigger();
            primaryHover.Conditions.Add(new Condition(PrimaryProperty, true));
            primaryHover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
            primaryHover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(OnAccentKey), "hover"));
            primaryHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.16, "hover"));
            template.Triggers.Add(primaryHover);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(keys.Surface)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(keys.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Border)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Triggers.Add(When(UIElement.IsMouseOverProperty, true, new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Accent))));
            if (type == typeof(Button))
            {
                // The default button is primary unless it is also the cancel button.
                var main = new MultiTrigger();
                main.Conditions.Add(new Condition(Button.IsDefaultProperty, true));
                main.Conditions.Add(new Condition(Button.IsCancelProperty, false));
                main.Setters.Add(new Setter(PrimaryProperty, true));
                style.Triggers.Add(main);
            }
            style.Triggers.Add(When(PrimaryProperty, true,
                new Setter(Control.BackgroundProperty, new DynamicResourceExtension(keys.Accent)),
                new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Accent)),
                new Setter(Control.ForegroundProperty, new DynamicResourceExtension(OnAccentKey)),
                new Setter(Control.FontWeightProperty, FontWeights.SemiBold)));
            if (typeof(ToggleButton).IsAssignableFrom(type))
                style.Triggers.Add(When(ToggleButton.IsCheckedProperty, true,
                    new Setter(Control.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey)),
                    new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Accent)),
                    new Setter(Control.FontWeightProperty, FontWeights.SemiBold)));
            style.Triggers.Add(When(UIElement.IsEnabledProperty, false, new Setter(UIElement.OpacityProperty, 0.45)));
            return style;
        }

        /// <summary>Flat tab headers with an accent underline on the selected tab; native tabs keep light gradients in dark palettes.</summary>
        internal static Style TabItemStyle(ThemeKeys keys, Style basedOn)
        {
            var style = new Style(typeof(TabItem), basedOn);
            if (SystemParameters.HighContrast) return style;
            var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
            chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4, 4, 0, 0));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chrome.AppendChild(header);
            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = chrome };
            template.Triggers.Add(When(UIElement.IsMouseOverProperty, true, new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey), "chrome")));
            template.Triggers.Add(When(UIElement.IsKeyboardFocusedProperty, true, new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey), "chrome")));
            template.Triggers.Add(When(TabItem.IsSelectedProperty, true, new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(keys.Accent), "chrome")));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(MutedKey)));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Triggers.Add(When(TabItem.IsSelectedProperty, true,
                new Setter(Control.ForegroundProperty, new DynamicResourceExtension(keys.Text)),
                new Setter(Control.FontWeightProperty, FontWeights.SemiBold)));
            style.Triggers.Add(When(UIElement.IsEnabledProperty, false, new Setter(UIElement.OpacityProperty, 0.45)));
            return style;
        }

        /// <summary>Tab body with a single rule under the headers.</summary>
        internal static Style TabControlStyle(ThemeKeys keys, Style basedOn)
        {
            var style = new Style(typeof(TabControl), basedOn);
            if (SystemParameters.HighContrast) return style;
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(keys.Back)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(keys.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Border)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 1, 0, 0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 10, 0, 0)));
            return style;
        }

        /// <summary>Rounded list rows: accent wash on hover, accent bar and wash when selected.</summary>
        internal static Style ListItemStyle(ThemeKeys keys, Style basedOn)
        {
            var style = new Style(typeof(ListBoxItem), basedOn);
            if (SystemParameters.HighContrast) return style;
            var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var grid = new FrameworkElementFactory(typeof(Grid));
            var bar = new FrameworkElementFactory(typeof(Border), "bar");
            bar.SetValue(FrameworkElement.WidthProperty, 3.0);
            bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
            bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
            bar.SetResourceReference(Border.BackgroundProperty, keys.Accent);
            bar.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            grid.AppendChild(bar); grid.AppendChild(content); chrome.AppendChild(grid);
            var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome };
            template.Triggers.Add(When(UIElement.IsMouseOverProperty, true, new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey), "chrome")));
            template.Triggers.Add(When(ListBoxItem.IsSelectedProperty, true,
                new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey), "chrome"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "bar")));
            template.Triggers.Add(When(UIElement.IsKeyboardFocusedProperty, true, new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(keys.Accent), "chrome")));
            template.Triggers.Add(When(UIElement.IsEnabledProperty, false, new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension(MutedKey), "chrome")));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1)));
            return style;
        }

        /// <summary>List and grid surfaces for fixed palettes; the native templates hardcode white.</summary>
        internal static Style ListStyle(Type type, ThemeKeys keys, Style basedOn)
        {
            var style = new Style(type, basedOn);
            if (!Overridden) return style;
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(keys.Surface)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(keys.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Border)));
            return style;
        }

        private const string XamlNamespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        private static readonly Lazy<ControlTemplate> HeaderTemplate = new Lazy<ControlTemplate>(() => (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate TargetType='GridViewColumnHeader' " + XamlNamespaces + "><Grid>" +
            "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='0,0,1,1' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/></Border>" +
            "<Thumb x:Name='PART_HeaderGripper' HorizontalAlignment='Right' Width='8' Margin='0,0,-4,0' Cursor='SizeWE'><Thumb.Template>" +
            "<ControlTemplate TargetType='Thumb'><Border Background='Transparent'/></ControlTemplate></Thumb.Template></Thumb></Grid></ControlTemplate>"));

        /// <summary>Flat column headers for fixed palettes; the native header uses a light gradient.</summary>
        internal static Style ColumnHeaderStyle(ThemeKeys keys, Style basedOn)
        {
            var style = new Style(typeof(GridViewColumnHeader), basedOn);
            if (!Overridden) return style;
            style.Setters.Add(new Setter(Control.TemplateProperty, HeaderTemplate.Value));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(keys.Back)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(keys.Text)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(keys.Border)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            return style;
        }

        private static ControlTemplate ScrollTemplate(bool vertical) => (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate TargetType='ScrollBar' " + XamlNamespaces + "><Border Background='{TemplateBinding Background}'>" +
            "<Track x:Name='PART_Track' Orientation='" + (vertical ? "Vertical' IsDirectionReversed='True'" : "Horizontal'") + ">" +
            "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar." + (vertical ? "PageUpCommand" : "PageLeftCommand") + "' Opacity='0' Focusable='False' IsTabStop='False'/></Track.DecreaseRepeatButton>" +
            "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Border x:Name='grip' CornerRadius='4' Margin='3' Background='{DynamicResource {x:Static SystemColors.ControlDarkBrushKey}}'/>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='grip' Property='Background' Value='{DynamicResource {x:Static SystemColors.GrayTextBrushKey}}'/></Trigger>" +
            "<Trigger Property='IsDragging' Value='True'><Setter TargetName='grip' Property='Background' Value='{DynamicResource {x:Static SystemColors.GrayTextBrushKey}}'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
            "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar." + (vertical ? "PageDownCommand" : "PageRightCommand") + "' Opacity='0' Focusable='False' IsTabStop='False'/></Track.IncreaseRepeatButton>" +
            "</Track></Border></ControlTemplate>");
        private static readonly Lazy<ControlTemplate> VerticalScroll = new Lazy<ControlTemplate>(() => ScrollTemplate(true));
        private static readonly Lazy<ControlTemplate> HorizontalScroll = new Lazy<ControlTemplate>(() => ScrollTemplate(false));

        /// <summary>Slim scroll bars for fixed palettes; the native bars stay light in dark palettes.</summary>
        internal static Style ScrollBarStyle(Style basedOn)
        {
            var style = new Style(typeof(ScrollBar), basedOn);
            if (!Overridden) return style;
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.TemplateProperty, VerticalScroll.Value));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 12.0));
            style.Triggers.Add(When(ScrollBar.OrientationProperty, Orientation.Horizontal,
                new Setter(Control.TemplateProperty, HorizontalScroll.Value),
                new Setter(FrameworkElement.WidthProperty, double.NaN), new Setter(FrameworkElement.MinWidthProperty, 0.0),
                new Setter(FrameworkElement.HeightProperty, 12.0), new Setter(FrameworkElement.MinHeightProperty, 12.0)));
            return style;
        }

        /// <summary>Chevron expander header with a hover wash; the native header draws a white circle on every palette.</summary>
        internal static Style ExpanderStyle(ThemeKeys keys, Style basedOn)
        {
            var style = new Style(typeof(Expander), basedOn);
            if (SystemParameters.HighContrast) return style;
            var chrome = new FrameworkElementFactory(typeof(Border), "chrome");
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            chrome.SetValue(Border.PaddingProperty, new Thickness(4, 3, 8, 3));
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var chevron = new FrameworkElementFactory(typeof(Path), "chevron");
            chevron.SetValue(Path.DataProperty, Geometry.Parse("M 2,0.5 L 6,4.5 L 2,8.5"));
            chevron.SetValue(Shape.StrokeProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            chevron.SetValue(Shape.StrokeThicknessProperty, 1.5);
            chevron.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            chevron.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            chevron.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            chevron.SetValue(FrameworkElement.WidthProperty, 9.0);
            chevron.SetValue(FrameworkElement.HeightProperty, 9.0);
            chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 8, 0));
            chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chevron.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            var label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            row.AppendChild(chevron); row.AppendChild(label); chrome.AppendChild(row);
            var header = new ControlTemplate(typeof(ToggleButton)) { VisualTree = chrome };
            header.Triggers.Add(When(ToggleButton.IsCheckedProperty, true, new Setter(UIElement.RenderTransformProperty, new RotateTransform(90), "chevron")));
            header.Triggers.Add(When(UIElement.IsMouseOverProperty, true, new Setter(Border.BackgroundProperty, new DynamicResourceExtension(AccentSoftKey), "chrome")));
            header.Triggers.Add(When(UIElement.IsKeyboardFocusedProperty, true, new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(keys.Accent), "chrome")));

            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var dock = new FrameworkElementFactory(typeof(DockPanel));
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(DockPanel.DockProperty, Dock.Top);
            toggle.SetValue(Control.TemplateProperty, header);
            toggle.SetValue(ContentControl.ContentProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderProperty));
            toggle.SetValue(ContentControl.ContentTemplateProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderTemplateProperty));
            toggle.SetValue(Control.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            toggle.SetValue(FrameworkElement.MinHeightProperty, 0.0);
            toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            toggle.SetValue(FrameworkElement.FocusVisualStyleProperty, null);
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Expander.IsExpanded)) { Mode = BindingMode.TwoWay, RelativeSource = RelativeSource.TemplatedParent });
            var body = new FrameworkElementFactory(typeof(ContentPresenter), "body");
            body.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            body.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            dock.AppendChild(toggle); dock.AppendChild(body); frame.AppendChild(dock);
            var template = new ControlTemplate(typeof(Expander)) { VisualTree = frame };
            template.Triggers.Add(When(Expander.IsExpandedProperty, true, new Setter(UIElement.VisibilityProperty, Visibility.Visible, "body")));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        /// <summary>Native combo templates paint their own surfaces, including the editable text box.</summary>
        internal static void PaintPicker(ComboBox picker, ThemeKeys keys)
        {
            picker.ApplyTemplate(); Paint(picker);
            void Paint(DependencyObject node)
            {
                if (node is Border border) border.SetResourceReference(Border.BackgroundProperty, keys.Surface);
                if (node is Control control)
                {
                    control.SetResourceReference(Control.BackgroundProperty, keys.Surface);
                    control.SetResourceReference(Control.ForegroundProperty, keys.Text);
                }
                if (node is Path glyph) glyph.SetResourceReference(Shape.FillProperty, keys.Text);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Paint(VisualTreeHelper.GetChild(node, i));
            }
        }

        /// <summary>Icon drawn at <paramref name="size"/> in the given brush.</summary>
        internal static FrameworkElement Glyph(Geometry icon, object brushKey, double size = 16)
        {
            var path = new Path { Data = icon };
            path.SetResourceReference(Shape.FillProperty, brushKey);
            var canvas = new Canvas { Width = Icons.Size, Height = Icons.Size };
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        }

        private static Trigger When(DependencyProperty property, object value, params SetterBase[] setters)
        {
            var trigger = new Trigger { Property = property, Value = value };
            foreach (var setter in setters) trigger.Setters.Add(setter);
            return trigger;
        }

        private static Color Lookup(FrameworkElement root, object key, Color fallback) => root.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
        private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
            (byte)Math.Round(a.R * amount + b.R * (1 - amount)), (byte)Math.Round(a.G * amount + b.G * (1 - amount)), (byte)Math.Round(a.B * amount + b.B * (1 - amount)));
        // White text when it meets AA on the fill, else whichever ink reads better; light dark-mode accents get dark text.
        private static Color ReadableOn(Color fill)
        {
            Color ink = C(0x111827);
            double fillLuminance = Luminance(fill), white = 1.05 / (fillLuminance + 0.05), dark = (fillLuminance + 0.05) / (Luminance(ink) + 0.05);
            return white >= 4.5 || white >= dark ? Colors.White : ink;
        }

        private static double Luminance(Color c)
        {
            double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }
}
