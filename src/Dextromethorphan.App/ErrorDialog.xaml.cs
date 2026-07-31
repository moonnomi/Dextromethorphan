using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Dextromethorphan.App;

internal enum ErrorDialogResult
{
    Continue,
    Restart,
    Exit
}

public partial class ErrorDialog : Window
{
    private readonly string _logs;

    public ErrorDialog(
        Exception exception,
        string logs,
        bool canContinue,
        string context)
    {
        InitializeComponent();
        _logs = logs;
        ContextText = context;
        MessageText = exception.GetBaseException().Message;
        DetailsText = exception.ToString();
        ContinueVisibility = canContinue ? Visibility.Visible : Visibility.Collapsed;
        DataContext = this;
    }

    public string ContextText { get; }
    public string MessageText { get; }
    public string DetailsText { get; }
    public Visibility ContinueVisibility { get; }
    internal ErrorDialogResult Result { get; private set; } = ErrorDialogResult.Exit;

    internal static ErrorDialogResult Show(
        Window? owner,
        Exception exception,
        string logs,
        bool canContinue,
        string context)
    {
        var dialog = new ErrorDialog(exception, logs, canContinue, context);
        if (owner?.IsLoaded == true) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsText); }
        catch { }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logs);
            Process.Start(new ProcessStartInfo("explorer.exe", _logs)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Result = ErrorDialogResult.Continue;
        DialogResult = true;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        Result = ErrorDialogResult.Restart;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Result = ErrorDialogResult.Exit;
        DialogResult = false;
    }
}

internal static class ErrorContinuationPolicy
{
    public static bool CanContinue(Exception exception)
    {
        var root = exception.GetBaseException();
        return root is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or FormatException
            or TimeoutException
            or OperationCanceledException;
    }
}
