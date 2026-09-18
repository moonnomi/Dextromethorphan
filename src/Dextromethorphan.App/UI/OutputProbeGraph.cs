using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.UI;

/// <summary>Bounded, presentation-only RMS history. Never reads or modifies audio buffers.</summary>
public sealed class OutputProbeGraph : FrameworkElement
{
    public static readonly DependencyProperty DiagnosticsProperty = DependencyProperty.Register(
        nameof(Diagnostics), typeof(AudioDiagnostics), typeof(OutputProbeGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public AudioDiagnostics? Diagnostics { get => (AudioDiagnostics?)GetValue(DiagnosticsProperty); set => SetValue(DiagnosticsProperty, value); }
    private readonly Queue<(double Time, double Outgoing, double Incoming, double Output)> _history = new();
    private long _lastFrames;
    protected override AutomationPeer OnCreateAutomationPeer() => new ProbePeer(this);
    private sealed class ProbePeer(OutputProbeGraph owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Diagnostics?.OutputMeasurement is { Available: true } output
            ? $"Audio output: {output.Frames} submitted frames, RMS {output.Rms:0.000000}, peak {output.Peak:0.000000}, {output.NonFiniteSamples} invalid samples. Mixed frames: {owner.Diagnostics.CrossfadeMeasurement?.MixedFrames ?? 0}."
            : "Audio output measurement unavailable. Play PCM audio.";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var graph = (OutputProbeGraph)owner;
        if (!graph.IsVisible || args.NewValue is not AudioDiagnostics { OutputMeasurement: { Available: true } output } diagnostics) return;
        if (output.Frames < graph._lastFrames) graph._history.Clear();
        if (output.Frames == graph._lastFrames) return;
        graph._lastFrames = output.Frames;
        var time = output.Frames / (double)Math.Max(1, diagnostics.OutputFormat?.SampleRate ?? 1);
        graph._history.Enqueue((time, diagnostics.CrossfadeMeasurement?.OutgoingRms ?? 0,
            diagnostics.CrossfadeMeasurement?.IncomingRms ?? 0, output.Rms));
        while (graph._history.Count > 300 || graph._history.TryPeek(out var first) && time - first.Time > 30) graph._history.Dequeue();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var muted = TryFindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.MediumPurple;
        void Label(string value, double x, double y, Brush brush, double size = 12) => dc.DrawText(
            new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, ActualWidth - x), Trimming = TextTrimming.CharacterEllipsis }, new Point(x, y));
        Label("Measured audio · last 30 seconds", 0, 0, text, 14);
        if (Diagnostics?.OutputMeasurement is not { Available: true } output)
        {
            Label("Play PCM audio to see output measurements.", 0, 30, muted);
            return;
        }
        var mix = Diagnostics.CrossfadeMeasurement;
        Label(mix?.Overlapping == true ? "Mixing both tracks" : $"Mixed frames: {mix?.MixedFrames ?? 0:N0}", 0, 24, accent);
        var history = _history.ToArray();
        var end = history.Length > 0 ? history[^1].Time : 0;
        for (var lane = 0; lane < 3; lane++)
        {
            var top = 52 + lane * 66;
            var value = lane == 0 ? mix?.OutgoingRms ?? 0 : lane == 1 ? mix?.IncomingRms ?? 0 : output.Rms;
            var name = lane == 0 ? "Outgoing (weighted)" : lane == 1 ? "Incoming (weighted)" : "WASAPI input (post-DSP)";
            Label($"{name}   {(value > 0 ? (20 * Math.Log10(value)).ToString("0.0") : "−∞")} dBFS", 0, top, text);
            var pen = new Pen(muted, .5);
            dc.DrawLine(pen, new Point(0, top + 54), new Point(ActualWidth, top + 54));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var started = false;
                foreach (var point in history)
                {
                    var rms = lane == 0 ? point.Outgoing : lane == 1 ? point.Incoming : point.Output;
                    var level = Math.Clamp((20 * Math.Log10(Math.Max(1e-6, rms)) + 60) / 60, 0, 1);
                    var position = new Point(Math.Clamp(1 - (end - point.Time) / 30, 0, 1) * ActualWidth, top + 54 - level * 32);
                    if (!started) { context.BeginFigure(position, false, false); started = true; }
                    else context.LineTo(position, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 1.5), geometry);
        }
        Label($"Submitted: {output.Frames:N0} frames · peak {output.Peak:0.0000}", 0, 256, text);
        Label($"Non-finite samples: {output.NonFiniteSamples:N0} · scale −60 to 0 dBFS", 0, 276, muted);
    }
}
