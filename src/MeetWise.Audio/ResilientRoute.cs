using MeetWise.Core.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetWise.Audio;

/// <summary>
/// Keeps an audio route alive for as long as the app runs: it is rebuilt when a device is unplugged or plugged in, when the Windows default changes,
/// or when capture or playback fails, retrying with growing delays while the microphone is missing. The microphone a user clicks "join" with
/// must keep working, so a route that dies quietly is not acceptable.
/// </summary>
public sealed class ResilientRoute : IDisposable
{
    private readonly Func<IAudioRoute> _factory;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly object _gate = new();
    private IAudioRoute? _route;
    private Timer? _retry;
    private int _attempt;
    private bool _disposed;

    public string Status { get; private set; } = "stopped";
    public int Restarts { get; private set; }
    public double RecentLoad => _route?.RecentLoad ?? 0;
    public long Overflows => _route?.Overflows ?? 0;
    public bool IsRunning { get { lock (_gate) return _route is not null; } }

    public event Action<string>? StatusChanged;

    public ResilientRoute(Func<IAudioRoute> factory, Func<int, TimeSpan>? backoff = null)
    {
        _factory = factory;
        _backoff = backoff ?? (n => TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Max(0, n - 1)))));
    }

    public void Start() => Rebuild("starting");

    /// <summary>Called when the set of audio devices or the Windows default changed: rebuild against the new situation.</summary>
    public void DevicesChanged() => Rebuild("devices changed");

    private void Rebuild(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _retry?.Dispose();
            _retry = null;
            DisposeRoute();
            try
            {
                var route = _factory();
                route.Failed += OnFailed;
                route.Start();
                _route = route;
                _attempt = 0;
                if (reason != "starting") Restarts++;
                SetStatus("running");
            }
            catch (Exception ex)
            {
                _attempt++;
                var delay = _backoff(_attempt);
                AppLog.Warn($"Route could not start ({reason}): {ex.Message}. Retrying in {delay.TotalSeconds:0.#} s.");
                SetStatus($"waiting for the microphone (retry {_attempt})");
                _retry = new Timer(_ => Rebuild("retry"), null, delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnFailed(string message)
    {
        AppLog.Warn("Route failed: " + message);
        // never rebuild on the audio thread that reported the failure
        _ = Task.Run(() => Rebuild("failure"));
    }

    private void DisposeRoute()
    {
        var r = _route;
        _route = null;
        if (r is null) return;
        r.Failed -= OnFailed;
        try { r.Dispose(); } catch (Exception ex) { AppLog.Warn("Route dispose failed: " + ex.Message); }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retry?.Dispose();
            DisposeRoute();
            Status = "stopped";
        }
    }
}

/// <summary>Raises one callback (debounced) when Windows adds, removes or switches an audio device.</summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Listener _listener;
    private readonly Timer _debounce;
    private readonly Action _changed;
    private readonly TimeSpan _quiet;

    public DeviceWatcher(Action changed, TimeSpan? quiet = null)
    {
        _changed = changed;
        _quiet = quiet ?? TimeSpan.FromMilliseconds(700);
        _debounce = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);
        _listener = new Listener(this);
        _enumerator.RegisterEndpointNotificationCallback(_listener);
    }

    public void Poke() => _debounce.Change(_quiet, Timeout.InfiniteTimeSpan); // a burst of events becomes one callback

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_listener); } catch (Exception) { }
        _debounce.Dispose();
        _enumerator.Dispose();
    }

    private sealed class Listener : IMMNotificationClient
    {
        private readonly DeviceWatcher _owner;
        public Listener(DeviceWatcher owner) => _owner = owner;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.Poke();
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.Poke();
        public void OnDeviceRemoved(string deviceId) => _owner.Poke();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.Poke();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* names and formats do not need a rebuild */ }
    }
}
