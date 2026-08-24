namespace Dextromethorphan.App.ViewModels;

public enum ToastSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record ToastNotification(
    string Message,
    ToastSeverity Severity = ToastSeverity.Information)
{
    public string Glyph => Severity switch
    {
        ToastSeverity.Success => "\uE73E",
        ToastSeverity.Warning => "\uE7BA",
        ToastSeverity.Error => "\uEA39",
        _ => "\uE946"
    };

    public string AutomationText => $"{Severity}: {Message}";

    public TimeSpan DisplayDuration => Severity == ToastSeverity.Error
        ? TimeSpan.FromSeconds(6)
        : TimeSpan.FromSeconds(3.2);
}
