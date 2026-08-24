using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dextromethorphan.App;

internal static class TextPromptDialog
{
    public static string? Show(Window owner, string title, string label, string initialValue = "")
    {
        var input = new TextBox { Text = initialValue, MinWidth = 320, Margin = new Thickness(0, 8, 0, 18) };
        AutomationProperties.SetName(input, label);
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Width = 390,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = owner.TryFindResource("BackgroundBrush") as System.Windows.Media.Brush,
            Foreground = owner.TryFindResource("TextBrush") as System.Windows.Media.Brush
        };
        var ok = new Button { Content = "_Save", MinWidth = 82, IsDefault = true };
        var cancel = new Button { Content = "_Cancel", MinWidth = 82, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(ok, "Save");
        AutomationProperties.SetName(cancel, "Cancel");
        AutomationProperties.SetName(dialog, title);
        KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
        ok.Click += (_, _) => dialog.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            dialog.DialogResult = true;
            e.Handled = true;
        };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }
}
