using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Dextromethorphan.App.UI;

public static class ArtworkGlow
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ArtworkGlow), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty TintProperty = DependencyProperty.RegisterAttached("Tint", typeof(Brush), typeof(ArtworkGlow), new PropertyMetadata(Brushes.CornflowerBlue, Changed));
    public static readonly DependencyProperty ArtworkPathProperty = DependencyProperty.RegisterAttached("ArtworkPath", typeof(string), typeof(ArtworkGlow), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(ArtworkGlow), new PropertyMetadata(22d, Changed));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static Brush GetTint(DependencyObject obj) => (Brush)obj.GetValue(TintProperty);
    public static void SetTint(DependencyObject obj, Brush value) => obj.SetValue(TintProperty, value);
    public static string? GetArtworkPath(DependencyObject obj) => (string?)obj.GetValue(ArtworkPathProperty);
    public static void SetArtworkPath(DependencyObject obj, string? value) => obj.SetValue(ArtworkPathProperty, value);
    public static double GetRadius(DependencyObject obj) => (double)obj.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject obj, double value) => obj.SetValue(RadiusProperty, value);

    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        if (!GetEnabled(obj) || string.IsNullOrWhiteSpace(GetArtworkPath(obj)) || SystemParameters.HighContrast)
        {
            element.Effect = null;
            return;
        }
        var shadow = new DropShadowEffect { BlurRadius = GetRadius(obj), ShadowDepth = 0, Opacity = .5, RenderingBias = RenderingBias.Performance };
        if (GetTint(obj) is SolidColorBrush brush)
            BindingOperations.SetBinding(shadow, DropShadowEffect.ColorProperty, new Binding(nameof(SolidColorBrush.Color)) { Source = brush });
        element.Effect = shadow;
    }
}
