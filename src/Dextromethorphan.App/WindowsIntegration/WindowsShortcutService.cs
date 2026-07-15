using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.App.WindowsIntegration;

public sealed class WindowsShortcutService : IShortcutService
{
    private const int WmHotKey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly ISettingsService _settings;
    private readonly Dictionary<int, string> _globalActions = [];
    private readonly Dictionary<ShortcutGesture, string> _inAppActions = [];
    private readonly List<ShortcutRegistrationResult> _registrations = [];
    private HwndSource? _source;
    private nint _windowHandle;
    private int _nextId = 0x4000;
    private bool _disposed;

    public WindowsShortcutService(ISettingsService settings)
    {
        _settings = settings;
        _settings.Changed += SettingsOnChanged;
    }

    public event EventHandler<string>? ActionInvoked;
    public IReadOnlyList<ShortcutRegistrationResult> Registrations => _registrations;

    public void Attach(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == 0) throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        if (_windowHandle == windowHandle) return;
        DetachWindow();
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("The WPF window source is not available.");
        _source.AddHook(WindowHook);
        Refresh(_settings.Current.Shortcuts);
    }

    public void Refresh(IEnumerable<ShortcutBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();
        _inAppActions.Clear();
        _registrations.Clear();
        var globalGestures = new HashSet<ShortcutGesture>();
        foreach (var binding in bindings.Where(x => x.Enabled))
        {
            if (!ShortcutGesture.TryParse(binding.Gesture, out var gesture))
            {
                _registrations.Add(new ShortcutRegistrationResult(binding.Action, binding.Gesture, binding.Global, false, "Invalid gesture"));
                continue;
            }
            if (!binding.Global)
            {
                if (!_inAppActions.TryAdd(gesture, binding.Action))
                    _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), false, false, "Gesture conflicts with another in-app action"));
                else
                    _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), false, true));
                continue;
            }
            if (!globalGestures.Add(gesture))
            {
                _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), true, false, "Gesture conflicts with another global action"));
                continue;
            }
            if (_windowHandle == 0)
            {
                _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), true, false, "Window is not attached"));
                continue;
            }
            var id = _nextId++;
            var modifiers = (uint)gesture.Modifiers | ModNoRepeat;
            if (RegisterHotKey(_windowHandle, id, modifiers, (uint)gesture.VirtualKey))
            {
                _globalActions[id] = binding.Action;
                _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), true, true));
            }
            else
            {
                _registrations.Add(new ShortcutRegistrationResult(binding.Action, gesture.ToString(), true, false, "Windows or another application already owns this gesture"));
            }
        }
    }

    public bool TryGetInAppAction(ShortcutGesture gesture, out string action) => _inAppActions.TryGetValue(gesture, out action!);

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && _globalActions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            ActionInvoked?.Invoke(this, action);
        }
        return 0;
    }

    private void SettingsOnChanged(object? sender, AppSettings settings)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => Refresh(settings.Shortcuts));
        else
            Refresh(settings.Shortcuts);
    }

    private void UnregisterAll()
    {
        if (_windowHandle != 0)
            foreach (var id in _globalActions.Keys) UnregisterHotKey(_windowHandle, id);
        _globalActions.Clear();
        _nextId = 0x4000;
    }

    private void DetachWindow()
    {
        UnregisterAll();
        if (_source is not null) _source.RemoveHook(WindowHook);
        _source = null;
        _windowHandle = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= SettingsOnChanged;
        DetachWindow();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
