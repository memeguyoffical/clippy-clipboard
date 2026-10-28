namespace Clippy.App.Services;

/// <summary>
/// Ensures one Clippy per user session. A second launch signals the running
/// instance to show the popup or the settings sheet, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Clippy.Instance.5F1B";
    private const string ActivateEventName = @"Local\Clippy.Activate.5F1B";
    private const string SettingsEventName = @"Local\Clippy.Settings.5F1B";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly EventWaitHandle _settingsEvent;
    private Thread? _listener;
    private volatile bool _stopping;
    private bool _disposed;

    internal event Action? ActivateRequested;

    internal event Action? SettingsRequested;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent, EventWaitHandle settingsEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _settingsEvent = settingsEvent;
    }

    /// <summary>Returns the instance, or <c>null</c> when another one already owns the mutex.</summary>
    internal static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(
            mutex,
            CreateEvent(ActivateEventName),
            CreateEvent(SettingsEventName));
    }

    /// <summary>Tells an already-running instance what to show. Best effort.</summary>
    internal static void SignalRunningInstance(StartupMode mode)
    {
        var name = mode == StartupMode.Settings ? SettingsEventName : ActivateEventName;

        if (EventWaitHandle.TryOpenExisting(name, out var existing))
        {
            using (existing)
            {
                existing.Set();
            }
        }
    }

    internal void StartListening()
    {
        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Clippy.ActivateListener",
        };

        _listener.Start();
    }

    private void ListenLoop()
    {
        var handles = new WaitHandle[] { _activateEvent, _settingsEvent };

        while (!_stopping)
        {
            var index = WaitHandle.WaitAny(handles, 250);

            if (_stopping || index == WaitHandle.WaitTimeout)
            {
                continue;
            }

            if (index == 0)
            {
                ActivateRequested?.Invoke();
            }
            else if (index == 1)
            {
                SettingsRequested?.Invoke();
            }
        }
    }

    private static EventWaitHandle CreateEvent(string name)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        catch (UnauthorizedAccessException)
        {
            // Another process created it with a different ACL; activation is optional.
            return new EventWaitHandle(false, EventResetMode.AutoReset, Guid.NewGuid().ToString("N"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _listener?.Join(TimeSpan.FromSeconds(2));
        _activateEvent.Dispose();
        _settingsEvent.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner; nothing to release.
        }

        _mutex.Dispose();
    }
}
