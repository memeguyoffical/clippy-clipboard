using Clippy.App.Interop;
using Clippy.App.Ui;
using Clippy.Core;
using Clippy.Core.Hotkey;
using Clippy.Core.Security;
using Clippy.Core.Settings;
using Clippy.Core.Storage;

namespace Clippy.App.Services;

/// <summary>Composition root: owns the store, capture, tray, hotkey and popup.</summary>
internal sealed class AppHost : ApplicationContext
{
    private readonly SingleInstance _instance;
    private readonly KeyRing _keys;
    private readonly ClipStore _store;
    private readonly CaptureService _capture;
    private readonly PopupForm _popup;
    private readonly TrayIcon _tray;
    private readonly GlobalHotkey _hotkey;
    private readonly UiDispatcher _dispatcher = new();
    private ClippySettings _settings;
    private HotkeyBinding _hotkeyBinding;
    private bool _disposed;

    internal AppHost(SingleInstance instance, StartupMode startup = StartupMode.None)
    {
        _instance = instance;
        _settings = ClippySettings.Load(ClippyPaths.Settings);

        _keys = KeyRing.LoadOrCreate(ClippyPaths.Root);
        _store = ClipStore.Open(ClippyPaths.Database, _keys);
        _capture = new CaptureService(_store, () => _settings);
        _popup = new PopupForm(_store, _capture, () => _settings, OpenSettings);

        _tray = new TrayIcon(
            ShowPopup,
            () => _settings.CaptureEnabled,
            ToggleCapture,
            OpenSettings,
            ClearHistory,
            ExitApplication);

        _hotkey = new GlobalHotkey(RequestPopup, _settings.Hotkey);
        _hotkeyBinding = _settings.Hotkey;

        _capture.ClipCaptured += OnHistoryChanged;
        _capture.HistoryChanged += OnHistoryChanged;
        _instance.ActivateRequested += RequestPopup;
        _instance.SettingsRequested += RequestSettings;
        _instance.StartListening();
        _capture.Start();

        PruneNow();
        AutoStart.Apply(_settings.LaunchAtLogin);

        var plan = _hotkey.Install();
        Log.Info($"Hotkey installed: {_hotkeyBinding.Describe()} strategy={plan.Strategy} win32={plan.Win32Error}");

        if (plan.PromptToDisableWindowsHistory && _hotkeyBinding.IsWindowsHistoryGesture)
        {
            _dispatcher.Post(ResolveWinVConflict);
        }

        Log.Info($"Ready: {_store.GetUsage().Count} clips loaded");

        switch (startup)
        {
            case StartupMode.Popup:
                _dispatcher.Post(ShowPopup);
                break;

            case StartupMode.Settings:
                _dispatcher.Post(OpenSettings);
                break;
        }
    }

    private void OnHistoryChanged(StoredClip _) => SafeReload();

    private void OnHistoryChanged() => SafeReload();

    private void SafeReload()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _popup.ReloadHistory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Popup torn down during shutdown.
        }
    }

    private void ResolveWinVConflict()
    {
        if (_disposed)
        {
            return;
        }

        if (!WinVConsent.ShouldAsk(_settings.WinVPromptDismissed))
        {
            Log.Info("Win+V conflict: prompt suppressed, keeping keyboard hook");
            return;
        }

        if (!WinVConsent.AskAndApply())
        {
            _settings.WinVPromptDismissed = true;
            Persist();
            Log.Info("Win+V conflict: user kept the keyboard hook");
            return;
        }

        var retry = _hotkey.RetryNative();
        Log.Info($"Win+V conflict: Windows history disabled, strategy={retry.Strategy} win32={retry.Win32Error}");
    }

    private void RequestPopup()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(TogglePopup);
    }

    private void RequestSettings()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Post(OpenSettings);
    }

    private void TogglePopup()
    {
        if (_disposed)
        {
            return;
        }

        _popup.ToggleAtCursor();
    }

    private void ShowPopup()
    {
        if (_disposed)
        {
            return;
        }

        _popup.ShowAtCursor();
    }

    private void ToggleCapture()
    {
        _settings.CaptureEnabled = !_settings.CaptureEnabled;
        Persist();
        Log.Info($"Capture enabled={_settings.CaptureEnabled}");
    }

    private void OpenSettings()
    {
        // Keep the sheet above the always-on-top popup when opened from it.
        using var form = new SettingsForm(_store, _settings, Persist)
        {
            TopMost = _popup.Visible,
        };

        form.ShowDialog();
        _popup.ReloadHistory();
    }

    private void Persist()
    {
        _settings.Save(ClippyPaths.Settings);
        AutoStart.Apply(_settings.LaunchAtLogin);
        ApplyHotkey();
        PruneNow();
    }

    /// <summary>
    /// Rebinds only when the stored gesture actually changed. Saving any other
    /// setting must not release and re-register the hotkey.
    /// </summary>
    private void ApplyHotkey()
    {
        var binding = _settings.Hotkey;

        if (binding == _hotkeyBinding)
        {
            return;
        }

        var plan = _hotkey.Rebind(binding);
        _hotkeyBinding = binding;
        Log.Info($"Hotkey changed: {binding.Describe()} strategy={plan.Strategy} win32={plan.Win32Error}");

        if (plan.PromptToDisableWindowsHistory && binding.IsWindowsHistoryGesture)
        {
            _dispatcher.Post(ResolveWinVConflict);
        }
    }

    private void PruneNow()
    {
        try
        {
            _store.Prune(_settings.ToRetentionPolicy(), DateTimeOffset.UtcNow);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Error("Prune failed", ex);
        }
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(
            "Delete all unpinned clips? Pinned clips are kept.",
            "Clippy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _store.Clear(includePinned: false);
        _popup.ReloadHistory();
        Log.Info("History cleared (pinned kept)");
    }

    private void ExitApplication()
    {
        _popup.Dismiss();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _instance.ActivateRequested -= RequestPopup;
            _instance.SettingsRequested -= RequestSettings;
            _capture.ClipCaptured -= OnHistoryChanged;
            _capture.HistoryChanged -= OnHistoryChanged;

            _hotkey.Dispose();
            _dispatcher.Dispose();
            _tray.Dispose();
            _capture.Dispose();
            _popup.Dispose();
            _store.Dispose();
            _keys.Dispose();
        }

        base.Dispose(disposing);
    }
}
