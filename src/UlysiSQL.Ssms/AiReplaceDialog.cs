using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UlysiSQL.Ssms
{
    internal sealed class AiReplaceDialog : Window
    {
        internal bool Apply { get; private set; }

        internal AiReplaceDialog()
        {
            Title = "UlysiSQL: review AI replacement";
            DialogParts.Style(this);
            Width = 520; Height = 300; MinWidth = 380; MinHeight = 290;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowActivated = false;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Review replacement", "Inspect the comparison tab while this window stays open. Apply replaces the original selection or script; Ctrl+Z undoes it.", Icons.Replace));
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_Apply", false, false, () => { Apply = true; Close(); }), ("_Cancel", true, true, Close))));
            Content = root;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }
    }
}
