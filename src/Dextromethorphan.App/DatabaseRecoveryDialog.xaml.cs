using System.IO;
using System.Windows;

namespace Dextromethorphan.App;

public enum DatabaseRecoveryChoice
{
    Exit,
    RestoreBackup,
    Rebuild
}

public partial class DatabaseRecoveryDialog : Window
{
    private DatabaseRecoveryDialog(
        Exception exception,
        string? latestBackup)
    {
        InitializeComponent();
        DetailText.Text =
            exception.GetBaseException().Message
            + "\n\nNo music file will be edited or deleted.";
        RestoreButton.IsEnabled = latestBackup is not null;
        BackupText.Text = latestBackup is null
            ? "No automatic backup is currently available."
            : $"Available: {Path.GetFileName(latestBackup)}";
    }

    public DatabaseRecoveryChoice Choice { get; private set; }

    public static DatabaseRecoveryChoice Show(
        Window owner,
        Exception exception,
        string? latestBackup)
    {
        var dialog = new DatabaseRecoveryDialog(
            exception,
            latestBackup)
        {
            Owner = owner
        };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        Choice = DatabaseRecoveryChoice.RestoreBackup;
        DialogResult = true;
    }

    private void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        Choice = DatabaseRecoveryChoice.Rebuild;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = DatabaseRecoveryChoice.Exit;
        DialogResult = false;
    }
}
