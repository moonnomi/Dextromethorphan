using System.Windows;

namespace Dextromethorphan.App;

public partial class ConfirmationDialog : Window
{
    private ConfirmationDialog(
        string title,
        string message,
        string confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public static bool Show(
        Window owner,
        string title,
        string message,
        string confirmText) =>
        new ConfirmationDialog(title, message, confirmText)
        {
            Owner = owner
        }.ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
