using System.Windows;
using Insomnia.App.Interop;
using Insomnia.App.Theme;

namespace Insomnia.App;

/// <summary>A modal yes/cancel question, themed like the rest of the app.</summary>
internal sealed partial class ConfirmDialog
{
    private ConfirmDialog(string heading, string message, string confirm)
    {
        InitializeComponent();
        ThemeManager.Current.Attach(this, Backdrop.Mica, managesFrame: true);
        Heading.Text = heading;
        Message.Text = message;
        ConfirmButton.Content = confirm;
        ConfirmButton.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Ask, and return true only when the user confirmed.</summary>
    public static bool Ask(string heading, string message, string confirm)
    {
        var dialog = new ConfirmDialog(heading, message, confirm);
        return dialog.ShowDialog() == true;
    }
}
