using System.Runtime.InteropServices;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Windows.Media;
using WinRT;

namespace Dextromethorphan.App.WindowsIntegration;

public sealed class SystemMediaTransportService : ISystemMediaTransportService
{
    private static readonly Guid InteropId = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");
    private static readonly Guid ControlsId = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
    private SystemMediaTransportControls? _controls;
    private string? _displayedTrackPath;
    private DateTimeOffset _lastTimelineUpdate;
    private PlaybackSnapshot? _pendingSnapshot;
    private bool _pendingHasPrevious;
    private bool _pendingHasNext;
    private bool _disposed;

    public event EventHandler<MediaTransportCommandEventArgs>? CommandReceived;
    public bool IsAvailable => _controls is not null;
    public string? Error { get; private set; }

    public void Attach(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controls is not null) return;
        if (windowHandle == 0) throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        nint className = 0;
        nint factoryPointer = 0;
        nint controlsPointer = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString("Windows.Media.SystemMediaTransportControls", 37, out className));
            var interopId = InteropId;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref interopId, out factoryPointer));
            var factory = (ISystemMediaTransportControlsInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            var controlsId = ControlsId;
            Marshal.ThrowExceptionForHR(factory.GetForWindow(windowHandle, ref controlsId, out controlsPointer));
            _controls = MarshalInterface<SystemMediaTransportControls>.FromAbi(controlsPointer);
            ConfigureControls(_controls);
            Error = null;
            if (_pendingSnapshot is not null) Update(_pendingSnapshot, _pendingHasPrevious, _pendingHasNext);
        }
        catch (Exception exception)
        {
            Error = "System media controls are unavailable: " + exception.Message;
            _controls = null;
        }
        finally
        {
            if (controlsPointer != 0) MarshalInterface<SystemMediaTransportControls>.DisposeAbi(controlsPointer);
            if (factoryPointer != 0) Marshal.Release(factoryPointer);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    public void Update(PlaybackSnapshot snapshot, bool hasPrevious, bool hasNext)
    {
        if (_disposed) return;
        _pendingSnapshot = snapshot;
        _pendingHasPrevious = hasPrevious;
        _pendingHasNext = hasNext;
        if (_controls is null) return;
        try
        {
            var hasTrack = snapshot.Track is not null;
            _controls.IsEnabled = hasTrack;
            _controls.IsPlayEnabled = hasTrack && snapshot.State is not PlaybackState.Playing;
            _controls.IsPauseEnabled = hasTrack && snapshot.State is PlaybackState.Playing;
            _controls.IsStopEnabled = hasTrack && snapshot.State is not PlaybackState.Stopped;
            _controls.IsPreviousEnabled = hasPrevious;
            _controls.IsNextEnabled = hasNext;
            _controls.PlaybackStatus = snapshot.State switch
            {
                PlaybackState.Playing => MediaPlaybackStatus.Playing,
                PlaybackState.Paused => MediaPlaybackStatus.Paused,
                PlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped
            };
            if (snapshot.Track is { } track && !string.Equals(_displayedTrackPath, track.Path, StringComparison.OrdinalIgnoreCase))
            {
                var updater = _controls.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.DisplayArtist;
                updater.MusicProperties.AlbumTitle = track.DisplayAlbum;
                updater.Update();
                _displayedTrackPath = track.Path;
            }
            var now = DateTimeOffset.UtcNow;
            if (now - _lastTimelineUpdate >= TimeSpan.FromMilliseconds(500) || snapshot.Position == TimeSpan.Zero)
            {
                var duration = snapshot.Duration < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Duration;
                var position = snapshot.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Position > duration ? duration : snapshot.Position;
                _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = position,
                    MaxSeekTime = duration,
                    EndTime = duration
                });
                _lastTimelineUpdate = now;
            }
        }
        catch (Exception exception)
        {
            Error = "Unable to update system media controls: " + exception.Message;
        }
    }

    private void ConfigureControls(SystemMediaTransportControls controls)
    {
        controls.IsEnabled = false;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsStopEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;
        controls.ButtonPressed += ControlsOnButtonPressed;
        controls.PlaybackPositionChangeRequested += ControlsOnPlaybackPositionChangeRequested;
    }

    private void ControlsOnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaTransportCommand.Play,
            SystemMediaTransportControlsButton.Pause => MediaTransportCommand.Pause,
            SystemMediaTransportControlsButton.Stop => MediaTransportCommand.Stop,
            SystemMediaTransportControlsButton.Next => MediaTransportCommand.Next,
            SystemMediaTransportControlsButton.Previous => MediaTransportCommand.Previous,
            _ => (MediaTransportCommand?)null
        };
        if (command.HasValue) CommandReceived?.Invoke(this, new MediaTransportCommandEventArgs(command.Value));
    }

    private void ControlsOnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        CommandReceived?.Invoke(this, new MediaTransportCommandEventArgs(MediaTransportCommand.Seek, args.RequestedPlaybackPosition));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_controls is not null)
        {
            _controls.ButtonPressed -= ControlsOnButtonPressed;
            _controls.PlaybackPositionChangeRequested -= ControlsOnPlaybackPositionChangeRequested;
            _controls.IsEnabled = false;
        }
        _controls = null;
    }

    [ComImport]
    [Guid("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface ISystemMediaTransportControlsInterop
    {
        [PreserveSig]
        int GetForWindow(nint appWindow, ref Guid interfaceId, out nint mediaTransportControl);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid interfaceId, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);
}
