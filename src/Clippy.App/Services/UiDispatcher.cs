using Clippy.App.Interop;

namespace Clippy.App.Services;

/// <summary>
/// Marshals work onto the UI thread through a message-only window.
/// Deliberately not a hidden Form: a hidden top-level form can be reported by
/// GetForegroundWindow as the foreground window, which corrupts both the
/// capture source and focus restoration.
/// </summary>
internal sealed class UiDispatcher : NativeWindow, IDisposable
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    internal UiDispatcher()
    {
        CreateHandle(new CreateParams
        {
            Caption = "ClippyDispatcher",
            Parent = NativeMethods.HWND_MESSAGE,
        });
    }

    /// <summary>Queues work on the UI thread. Safe to call from any thread.</summary>
    internal void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _queue.Enqueue(action);
        }

        NativeMethods.PostMessage(Handle, NativeMethods.WM_APP_DISPATCH, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_APP_DISPATCH)
        {
            Action[] pending;

            lock (_gate)
            {
                pending = [.. _queue];
                _queue.Clear();
            }

            foreach (var action in pending)
            {
                action();
            }

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
        DestroyHandle();
    }
}
