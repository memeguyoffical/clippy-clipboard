using Clippy.App.Interop;
using Clippy.Core.Settings;
using Clippy.Core.Storage;

namespace Clippy.App.Services;

/// <summary>
/// Listens for clipboard changes, filters them, and writes encrypted rows.
/// Event-driven: no polling timer runs while the clipboard is idle.
/// </summary>
internal sealed class CaptureService : IDisposable
{
    private readonly ClipStore _store;
    private readonly Func<ClippySettings> _settings;
    private readonly ClipboardListenerWindow _listener;
    private readonly System.Windows.Forms.Timer _debounce;
    private DateTime _suppressUntil;
    private bool _disposed;

    internal event Action<StoredClip>? ClipCaptured;

    internal event Action? HistoryChanged;

    internal CaptureService(ClipStore store, Func<ClippySettings> settings)
    {
        _store = store;
        _settings = settings;

        // A single copy can raise several updates; coalesce them.
        _debounce = new System.Windows.Forms.Timer { Interval = 90 };
        _debounce.Tick += OnDebounceElapsed;

        _listener = new ClipboardListenerWindow();
        _listener.ClipboardUpdated += OnClipboardUpdated;
    }

    internal void Start() => _listener.Start();

    /// <summary>Stops capture briefly so our own paste write is not re-recorded.</summary>
    internal void SuppressCaptureFor(TimeSpan duration) => _suppressUntil = DateTime.UtcNow + duration;

    private void OnClipboardUpdated()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounce.Stop();
        Capture();
    }

    internal void Capture()
    {
        if (DateTime.UtcNow < _suppressUntil)
        {
            return;
        }

        var settings = _settings();

        if (!settings.CaptureEnabled)
        {
            return;
        }

        var source = ClipboardAccess.GetForegroundInfo();

        // Our own process can be reported as the foreground window (a hidden
        // window still counts). Never attribute a capture to ourselves, and
        // never let that stop the capture.
        if (string.Equals(source.ProcessName, "Clippy", StringComparison.OrdinalIgnoreCase))
        {
            source = new ForegroundInfo(IntPtr.Zero, string.Empty, string.Empty);
        }

        if (IsExcluded(source.ProcessName, settings))
        {
            Log.Info($"Capture skipped: excluded process '{source.ProcessName}'");
            return;
        }

        var result = ClipboardAccess.Read(source);

        switch (result.Outcome)
        {
            case CaptureOutcome.Captured when result.Payload is not null:
                Store(result.Payload, settings);
                break;

            case CaptureOutcome.BlockedBySource:
                Log.Info("Capture skipped: source marked content as private");
                break;

            case CaptureOutcome.TooLarge:
                Log.Info("Capture skipped: payload above size limit");
                break;

            case CaptureOutcome.Busy:
                Log.Info("Capture skipped: clipboard busy");
                break;
        }
    }

    private void Store(Core.Models.ClipPayload payload, ClippySettings settings)
    {
        try
        {
            var added = _store.Add(payload, BuildPreview(payload));
            _store.Prune(settings.ToRetentionPolicy(), DateTimeOffset.UtcNow);

            if (added.IsNew)
            {
                ClipLog(added.Clip);
                ClipCaptured?.Invoke(added.Clip);
            }
            else
            {
                HistoryChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Failed to store clip", ex);
        }
    }

    private static void ClipLog(StoredClip clip)
    {
        // Metadata only. Clipboard contents never reach the log.
        Log.Info($"Captured kind={clip.Kind} bytes={clip.Bytes} app='{clip.SourceApp}'");
    }

    /// <summary>Nicer list text than raw paths or a bare "Image".</summary>
    private static string? BuildPreview(Core.Models.ClipPayload payload)
    {
        if (payload.Files is { Length: > 0 } files)
        {
            var names = string.Join(", ", files.Take(3).Select(Path.GetFileName));
            var extra = files.Length > 3 ? $" +{files.Length - 3} more" : string.Empty;
            return $"{files.Length} file{(files.Length == 1 ? string.Empty : "s")} · {names}{extra}";
        }

        if (payload.ImagePng is { Length: > 0 } png && TryReadPngSize(png, out var width, out var height))
        {
            return $"Image · {width} × {height}";
        }

        return null;
    }

    /// <summary>Reads width/height straight from the PNG IHDR header; no decode.</summary>
    private static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (png.Length < 24
            || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
        {
            return false;
        }

        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        return width > 0 && height > 0;
    }

    private static bool IsExcluded(string processName, ClippySettings settings)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        return settings.ExcludedProcesses.Any(
            excluded => string.Equals(excluded, processName, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Stop();
        _debounce.Dispose();
        _listener.Dispose();
    }
}

/// <summary>Message-only window that receives <c>WM_CLIPBOARDUPDATE</c>.</summary>
internal sealed class ClipboardListenerWindow : NativeWindow, IDisposable
{
    private bool _registered;
    private bool _disposed;

    internal event Action? ClipboardUpdated;

    internal void Start()
    {
        CreateHandle(new CreateParams
        {
            Caption = "ClippyClipboardListener",
            Parent = NativeMethods.HWND_MESSAGE,
        });

        _registered = NativeMethods.AddClipboardFormatListener(Handle);

        if (!_registered)
        {
            Log.Warn($"AddClipboardFormatListener failed (win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}); falling back to manual capture only");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardUpdated?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered)
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
            _registered = false;
        }

        DestroyHandle();
    }
}
