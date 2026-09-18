using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Microsoft.Win32;
using Track = Dextromethorphan.Core.Models.Track;

namespace Dextromethorphan.App;

public partial class MainWindow
{
    private void UpdateDebugContextMenu(FrameworkElement source, ContextMenu menu, bool fromPointer)
    {
        foreach (var old in menu.Items.OfType<MenuItem>().Where(x => Equals(x.Tag, "debug-actions")).ToArray()) menu.Items.Remove(old);
        if (!ViewModel.DebugMode) return;
        var context = source is Selector selector ? selector.SelectedItem : source.DataContext;
        if (fromPointer && source is Selector
            && FindVisualParent<ListBoxItem>(System.Windows.Input.Mouse.DirectlyOver as DependencyObject) is { } row)
            context = row.DataContext;
        var track = context switch
        {
            Track value => value,
            QueueEntryViewModel entry => entry.Track,
            LibraryCardViewModel card => card.RepresentativeTrack,
            _ => null
        };
        var debug = new MenuItem { Header = "Debug", Tag = "debug-actions" };
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) { ErrorDialog.Show(this, error, "", true, "Debug action failed"); }
            };
            debug.Items.Add(item);
        }
        Add("Open file in Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe",
            $"/select,\"{Path.GetFullPath(track!.EffectiveMediaPath)}\"") { UseShellExecute = true }), track is not null);
        Add("Copy file path", () => Clipboard.SetText(track!.EffectiveMediaPath), track is not null);
        Add(context is LibraryCardViewModel ? "Inspect representative track…" : "Inspect track metadata…",
            () => ShowTrackDebugReport(track!, false), track is not null);
        Add(context is LibraryCardViewModel ? "Test representative track decoder…" : "Test audio decoder…",
            () => ShowTrackDebugReport(track!, true), track is not null);
        debug.Items.Add(new Separator());
        Add("Show live output diagnostics", () => ViewModel.DiagnosticsVisible = true);
        menu.Items.Add(debug);
    }

    private async void ShowTrackDebugReport(Track track, bool testDecoder)
    {
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
            Text = "Running read-only test… No playback or library changes. Decoder calls may take time on slow storage." };
        var save = new Button { Content = "Save report…", IsEnabled = false, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var layout = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(save, Dock.Bottom); layout.Children.Add(save); layout.Children.Add(text);
        var window = new Window { Owner = this, Title = testDecoder ? "Audio decoder diagnostic" : "Track metadata",
            Width = 680, Height = 540, MinWidth = 420, MinHeight = 320, Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cancellation.Token;
        window.Closed += (_, _) => { cancellation.Cancel(); cancellation.Dispose(); };
        save.Click += async (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "track-diagnostic.json" };
            if (dialog.ShowDialog(window) != true) return;
            try { await File.WriteAllTextAsync(dialog.FileName, text.Text); }
            catch (Exception error) { ErrorDialog.Show(window, error, "", true, "Report could not be saved"); }
        };
        window.Show();
        try
        {
            var probe = testDecoder ? await AudioFileProbe.RunAsync(track, token) : null;
            if (!window.IsVisible) return;
            text.Text = JsonSerializer.Serialize(new
            {
                capturedAt = DateTimeOffset.Now,
                scope = "Read-only metadata and short head/tail decoder test. Not a full-file integrity or hardware-output test.",
                track.Id, track.Title, track.Artist, track.Album, track.Codec, track.SampleRate, track.BitsPerSample,
                track.Channels, track.Duration, track.EffectiveMediaPath, track.IsCueTrack, track.SegmentStart,
                track.SegmentEnd, track.ReplayGainTrackDb, track.ReplayGainAlbumDb, track.ReplayPeak,
                probe
            }, new JsonSerializerOptions { WriteIndented = true });
            save.IsEnabled = true;
        }
        catch (OperationCanceledException) { if (window.IsVisible) text.Text = "Test cancelled."; }
        catch (Exception error) { if (window.IsVisible) text.Text = error.ToString(); }
    }
}
