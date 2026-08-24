using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dextromethorphan.App.UI.Controls;

public enum UiStateKind
{
    Loading,
    Empty,
    Offline,
    Error,
    Disabled,
    Success
}

public partial class StatePresenter : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(UiStateKind),
            typeof(StatePresenter),
            new PropertyMetadata(UiStateKind.Empty, OnAccessibleContentChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(StatePresenter),
            new PropertyMetadata(string.Empty, OnAccessibleContentChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(StatePresenter),
            new PropertyMetadata(string.Empty, OnAccessibleContentChanged));

    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(
            nameof(ActionText),
            typeof(string),
            typeof(StatePresenter),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(
            nameof(ActionCommand),
            typeof(ICommand),
            typeof(StatePresenter));

    public static readonly DependencyProperty ActionCommandParameterProperty =
        DependencyProperty.Register(
            nameof(ActionCommandParameter),
            typeof(object),
            typeof(StatePresenter));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(StatePresenter),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey AccessibleNamePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(AccessibleName),
            typeof(string),
            typeof(StatePresenter),
            new PropertyMetadata("Status"));

    public static readonly DependencyProperty AccessibleNameProperty =
        AccessibleNamePropertyKey.DependencyProperty;

    public StatePresenter()
    {
        InitializeComponent();
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        UpdateAccessibleName();
    }

    public UiStateKind Kind
    {
        get => (UiStateKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public string AccessibleName => (string)GetValue(AccessibleNameProperty);

    private static void OnAccessibleContentChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((StatePresenter)dependencyObject).UpdateAccessibleName();

    private void UpdateAccessibleName()
    {
        var description = string.IsNullOrWhiteSpace(Description)
            ? string.Empty
            : $". {Description.Trim()}";
        SetValue(
            AccessibleNamePropertyKey,
            $"{Kind}: {Title?.Trim()}{description}".Trim());
    }
}
