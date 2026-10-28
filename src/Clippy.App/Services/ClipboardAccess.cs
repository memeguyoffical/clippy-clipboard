using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Clippy.App.Interop;
using Clippy.Core.Clipboard;
using Clippy.Core.Models;

namespace Clippy.App.Services;

internal enum CaptureOutcome
{
    Captured,
    Empty,
    Busy,
    BlockedBySource,
    TooLarge,
}

internal readonly record struct CaptureResult(CaptureOutcome Outcome, ClipPayload? Payload);

internal sealed record ForegroundInfo(IntPtr Handle, string ProcessName, string Title);

/// <summary>
/// Reads and writes the Windows clipboard.
/// </summary>
internal static class ClipboardAccess
{
    private const string CanIncludeFormat = "CanIncludeInClipboardHistory";
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";
    private const string PngFormat = "PNG";

    /// <summary>Anything bigger than this is skipped rather than encrypted and stored.</summary>
    private const long MaxPayloadBytes = 32L * 1024 * 1024;

    internal static ForegroundInfo GetForegroundInfo()
    {
        var handle = NativeMethods.GetForegroundWindow();

        if (handle == IntPtr.Zero)
        {
            return new ForegroundInfo(IntPtr.Zero, string.Empty, string.Empty);
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new ForegroundInfo(handle, process.ProcessName, process.MainWindowTitle);
        }
        catch (ArgumentException)
        {
            return new ForegroundInfo(handle, string.Empty, string.Empty);
        }
        catch (InvalidOperationException)
        {
            return new ForegroundInfo(handle, string.Empty, string.Empty);
        }
    }

    internal static CaptureResult Read(ForegroundInfo source)
    {
        var data = TryGetDataObject();

        if (data is null)
        {
            return new CaptureResult(CaptureOutcome.Busy, null);
        }

        if (IsBlockedBySource(data))
        {
            return new CaptureResult(CaptureOutcome.BlockedBySource, null);
        }

        var payload = new ClipPayload
        {
            Files = ReadFiles(data),
            ImagePng = ReadImage(data),
            Html = ReadHtml(data),
            Rtf = ReadRtf(data),
            Text = ReadText(data),
            SourceApp = source.ProcessName,
            SourceTitle = source.Title,
        };

        if (payload.IsEmpty)
        {
            return new CaptureResult(CaptureOutcome.Empty, null);
        }

        if (payload.EstimatedBytes > MaxPayloadBytes)
        {
            return new CaptureResult(CaptureOutcome.TooLarge, null);
        }

        return new CaptureResult(CaptureOutcome.Captured, payload);
    }

    internal static void Write(ClipPayload payload, bool plainTextOnly)
    {
        if (!plainTextOnly && payload.ImagePng is { Length: > 0 } png)
        {
            WriteImage(payload, png);
            return;
        }

        var data = new DataObject();
        var text = payload.Text ?? payload.SearchText ?? string.Empty;

        if (plainTextOnly)
        {
            data.SetText(text);
        }
        else
        {
            if (payload.Files is { Length: > 0 })
            {
                var collection = new StringCollection();
                collection.AddRange(payload.Files);
                data.SetFileDropList(collection);
            }

            if (!string.IsNullOrEmpty(payload.Html))
            {
                data.SetData(DataFormats.Html, CfHtml.Build(payload.Html));
            }

            if (!string.IsNullOrEmpty(payload.Rtf))
            {
                data.SetData(DataFormats.Rtf, payload.Rtf);
            }

            if (!string.IsNullOrEmpty(text))
            {
                data.SetText(text);
            }
        }

        MarkPrivate(data);

        Clipboard.SetDataObject(data, copy: true, retryTimes: 8, retryDelay: 40);
    }

    /// <summary>
    /// Marks a published clip so Windows clipboard history and cloud sync skip
    /// it, and so Clippy treats its own paste-back as private if it ever reads
    /// it again.
    /// </summary>
    private static void MarkPrivate(DataObject data)
    {
        data.SetData(CanIncludeFormat, 0);
        data.SetData(ExcludeFormat, new byte[] { 0, 0, 0, 0 });
        data.SetData(ViewerIgnoreFormat, new byte[] { 0, 0, 0, 0 });
    }

    private static readonly uint PngFormatId = NativeMethods.RegisterClipboardFormat(PngFormat);
    private static readonly uint HtmlFormatId = NativeMethods.RegisterClipboardFormat(DataFormats.Html);
    private static readonly uint RtfFormatId = NativeMethods.RegisterClipboardFormat(DataFormats.Rtf);
    private static readonly uint CanIncludeFormatId = NativeMethods.RegisterClipboardFormat(CanIncludeFormat);
    private static readonly uint ExcludeFormatId = NativeMethods.RegisterClipboardFormat(ExcludeFormat);
    private static readonly uint ViewerIgnoreFormatId = NativeMethods.RegisterClipboardFormat(ViewerIgnoreFormat);
    private static NativeWindow? _clipboardOwner;

    /// <summary>
    /// Publishes an image through Win32. A WinForms DataObject can only carry a
    /// managed Bitmap, which .NET Framework readers cannot deserialize, so it
    /// writes the native trio instead: CF_DIB with alpha, CF_BITMAP for older
    /// consumers, and PNG for browsers.
    /// </summary>
    private static void WriteImage(ClipPayload payload, byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);

        if (!TryOpenClipboard())
        {
            throw new ExternalException("The clipboard is in use by another process.", unchecked((int)0x800401D0));
        }

        try
        {
            NativeMethods.EmptyClipboard();

            // Same privacy markers the DataObject path sets.
            SetClipboardBytes(CanIncludeFormatId, new byte[4]);
            SetClipboardBytes(ExcludeFormatId, new byte[4]);
            SetClipboardBytes(ViewerIgnoreFormatId, new byte[4]);

            SetClipboardBytes(NativeMethods.CF_DIB, BuildDib(bitmap));
            SetClipboardBytes(PngFormatId, png);

            if (!string.IsNullOrEmpty(payload.Html))
            {
                SetClipboardBytes(HtmlFormatId, Encoding.UTF8.GetBytes(CfHtml.Build(payload.Html) + "\0"));
            }

            if (!string.IsNullOrEmpty(payload.Rtf))
            {
                SetClipboardBytes(RtfFormatId, Encoding.UTF8.GetBytes(payload.Rtf + "\0"));
            }

            if (!string.IsNullOrEmpty(payload.Text))
            {
                SetClipboardBytes(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes(payload.Text + "\0"));
            }

            var handle = CreateDdb(bitmap);

            if (handle == IntPtr.Zero || NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, handle) == IntPtr.Zero)
            {
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(handle);
                }

                Log.Warn($"CF_BITMAP not published (win32={Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Copies the image into a device-dependent bitmap. A DIB section cannot be
    /// shared through CF_BITMAP, and GDI+ hands one out for 32bpp sources, so the
    /// pixels are blitted into a screen-compatible bitmap instead.
    /// </summary>
    private static IntPtr CreateDdb(Bitmap source)
    {
        var screen = NativeMethods.GetDC(IntPtr.Zero);

        if (screen == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var memory = IntPtr.Zero;
        var handle = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memory = NativeMethods.CreateCompatibleDC(screen);
            handle = NativeMethods.CreateCompatibleBitmap(screen, source.Width, source.Height);

            if (memory == IntPtr.Zero || handle == IntPtr.Zero)
            {
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(handle);
                }

                return IntPtr.Zero;
            }

            previous = NativeMethods.SelectObject(memory, handle);

            using (var graphics = Graphics.FromHdc(memory))
            {
                graphics.Clear(Color.Black);
                graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
            }

            return handle;
        }
        finally
        {
            if (memory != IntPtr.Zero)
            {
                if (previous != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memory, previous);
                }

                NativeMethods.DeleteDC(memory);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(ClipboardOwner))
            {
                return true;
            }

            Thread.Sleep(30);
        }

        return false;
    }

    private static IntPtr ClipboardOwner
    {
        get
        {
            if (_clipboardOwner is null)
            {
                var window = new NativeWindow();
                window.CreateHandle(new CreateParams
                {
                    Caption = "ClippyClipboardOwner",
                    Parent = NativeMethods.HWND_MESSAGE,
                });

                _clipboardOwner = window;
            }

            return _clipboardOwner.Handle;
        }
    }

    private static void SetClipboardBytes(uint format, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);

        if (handle == IntPtr.Zero)
        {
            Log.Warn($"GlobalAlloc failed for clipboard format {format}");
            return;
        }

        var target = NativeMethods.GlobalLock(handle);

        if (target == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return;
        }

        Marshal.Copy(bytes, 0, target, bytes.Length);
        NativeMethods.GlobalUnlock(handle);

        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
        }
    }

    /// <summary>
    /// CF_DIB payload: a BMP file minus its 14-byte file header, which leaves
    /// the BITMAPINFOHEADER, optional palette and pixel rows.
    /// </summary>
    private static byte[] BuildDib(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);

        var bytes = stream.ToArray();

        if (bytes.Length <= 14 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return [];
        }

        return bytes[14..];
    }

    private static IDataObject? TryGetDataObject()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        return null;
    }

    /// <summary>
    /// Honours the opt-out markers that password managers and browsers set, so
    /// secrets never reach the database in the first place.
    /// </summary>
    private static bool IsBlockedBySource(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(ExcludeFormat) || data.GetDataPresent(ViewerIgnoreFormat))
            {
                return true;
            }

            if (!data.GetDataPresent(CanIncludeFormat))
            {
                return false;
            }

            return !AllowsCapture(data.GetData(CanIncludeFormat));
        }
        catch (ExternalException)
        {
            // If we cannot read the marker, assume it is safe to capture only when
            // the format was absent; an unreadable marker means "do not capture".
            return data.GetDataPresent(CanIncludeFormat);
        }
    }

    /// <summary>
    /// The marker is a DWORD. OLE hands registered formats back as a Stream in
    /// most real apps, so the old int-only check let password-manager copies
    /// through. Anything unrecognised counts as "do not capture".
    /// </summary>
    private static bool AllowsCapture(object? raw) => raw switch
    {
        int value => value != 0,
        byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0) != 0,
        byte[] bytes when bytes.Length > 0 => bytes[0] != 0,
        string text when int.TryParse(text, out var parsed) => parsed != 0,
        Stream stream => ReadMarkerDword(stream) != 0,
        _ => false,
    };

    private static int ReadMarkerDword(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer[read..]);

            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (read >= 4)
        {
            return BitConverter.ToInt32(buffer);
        }

        return read > 0 ? buffer[0] : 0;
    }

    private static string? ReadText(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.UnicodeText)
                ? data.GetData(DataFormats.UnicodeText) as string
                : data.GetDataPresent(DataFormats.Text)
                    ? data.GetData(DataFormats.Text) as string
                    : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static string? ReadHtml(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Html))
            {
                return null;
            }

            var raw = data.GetData(DataFormats.Html);
            var document = raw as string ?? ReadStreamAsString(raw);

            return CfHtml.ExtractFragment(document);
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static string? ReadRtf(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Rtf))
            {
                return null;
            }

            var raw = data.GetData(DataFormats.Rtf);
            return raw as string ?? ReadStreamAsString(raw);
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static string[]? ReadFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            return data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static byte[]? ReadImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(PngFormat) && data.GetData(PngFormat) is Stream pngStream)
            {
                using (pngStream)
                {
                    using var buffer = new MemoryStream();
                    pngStream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }

            if (!Clipboard.ContainsImage())
            {
                return null;
            }

            using var image = Clipboard.GetImage();

            if (image is null)
            {
                return null;
            }

            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadStreamAsString(object? raw)
    {
        if (raw is not Stream stream)
        {
            return raw as string;
        }

        using (stream)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>Restores focus to the window the user came from and sends Ctrl+V.</summary>
internal static class PasteInjector
{
    internal static bool TryPaste(IntPtr targetWindow, IntPtr restoreWindow = default)
    {
        try
        {
            if (targetWindow != IntPtr.Zero)
            {
                // The shell refuses a plain call while another process owns the
                // foreground. Join the input queues and wait until the target
                // really has focus, or browsers drop the keystroke.
                ForceForeground(targetWindow);
                WaitForForeground(targetWindow, 500);
            }

            var inputs = new NativeMethods.INPUT[4];

            inputs[0] = Key(NativeMethods.VK_CONTROL, up: false);
            inputs[1] = Key(NativeMethods.VK_V, up: false);
            inputs[2] = Key(NativeMethods.VK_V, up: true);
            inputs[3] = Key(NativeMethods.VK_CONTROL, up: true);

            var sent = NativeMethods.SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<NativeMethods.INPUT>());

            // Chromium and WinUI dispatch input asynchronously; give the target
            // time to read the clipboard before focus moves back.
            Thread.Sleep(160);

            // Hand focus back so Esc and typing keep working while the popup
            // stays open for the next pick.
            if (restoreWindow != IntPtr.Zero)
            {
                ForceForeground(restoreWindow);
            }

            return sent == inputs.Length;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private static NativeMethods.INPUT Key(int virtualKey, bool up) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KEYBDINPUT
            {
                VirtualKey = (ushort)virtualKey,
                Flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    private static void WaitForForeground(IntPtr window, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (NativeMethods.GetForegroundWindow() == window)
            {
                return;
            }

            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Brings a window to the foreground even when the shell's foreground lock
    /// would reject a plain call.
    /// </summary>
    private static bool ForceForeground(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();

        if (foreground == window)
        {
            return true;
        }

        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);

        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            return NativeMethods.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
