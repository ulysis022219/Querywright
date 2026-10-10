using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Querywright.Ssms
{
    internal sealed class AiReplaceDialog : Window
    {
        internal bool Apply { get; private set; }

        internal AiReplaceDialog()
        {
            Title = "Querywright: review AI replacement";
            DialogParts.Style(this);
            Width = 480; Height = 260; MinWidth = 360; MinHeight = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowActivated = false;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Review replacement", "Review changes in the comparison tab. You can scroll and inspect that tab while this window stays open. Apply replaces the original selection or script; Ctrl+Z undoes it."));
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_Apply", false, false, () => { Apply = true; Close(); }), ("_Cancel", true, true, Close))));
            Content = root;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }
    }
}
