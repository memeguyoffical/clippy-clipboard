using System.Drawing;
using System.Windows.Forms;
using Clippy.Core.Models;
using Clippy.Core.Storage;

namespace Clippy.App.Ui;

internal sealed class ClipRow
{
    internal required StoredClip Clip { get; init; }

    internal required string KindLabel { get; init; }

    internal required string TimeLabel { get; init; }

    internal static string KindLabelFor(StoredClip clip) => clip.Kind switch
    {
        Core.Models.ClipKind.Image => "IMAGE",
        Core.Models.ClipKind.Files => "FILES",
        Core.Models.ClipKind.Html => "HTML",
        Core.Models.ClipKind.RichText => "RTF",
        _ => "TEXT",
    };

    internal string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Clip.Nickname))
            {
                return Clip.Nickname;
            }

            if (Clip.Kind == Core.Models.ClipKind.Image)
            {
                // The row draws the picture itself; resolution is noise here.
                return "Image";
            }

            var preview = Clip.Preview.Replace("\r", " ").Replace("\n", " ").Trim();

            if (preview.Length == 0)
            {
                return KindLabelToTitle(KindLabel);
            }

            return preview;
        }
    }

    internal string Meta
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Clip.Nickname))
            {
                // The preview is deliberately hidden: a nickname exists so the
                // content (often a password) is not on screen.
                return Clip.Pinned
                    ? $"{KindLabel}  ·  {TimeLabel}  ·  pinned"
                    : $"{KindLabel}  ·  {TimeLabel}";
            }

            return Clip.Pinned
                ? $"{TimeLabel}  ·  pinned"
                : TimeLabel;
        }
    }

    private static string KindLabelToTitle(string kindLabel) => kindLabel switch
    {
        "IMAGE" => "Image",
        "FILES" => "Files",
        "HTML" => "Rich text from a web page",
        "RTF" => "Rich text",
        _ => "Empty clip",
    };
}

/// <summary>Owner-drawn list: flat, dark, no chrome.</summary>
internal sealed class ClipListBox : ListBox
{
    private const int ThumbnailCacheLimit = 100;

    private readonly Dictionary<long, Bitmap?> _thumbnails = [];

    // Owner-draw runs on every paint; allocating brushes there leaked GDI
    // handles until the list scrolled enough to exhaust the pool.
    private readonly SolidBrush _surfaceBrush = new(Theme.Surface);
    private readonly SolidBrush _selectionBrush = new(Theme.Selection);
    private readonly SolidBrush _raisedBrush = new(Theme.SurfaceRaised);
    private readonly SolidBrush _textBrush = new(Theme.Text);

    internal ClipListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        TabStop = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
    }

    /// <summary>Decrypts a clip payload on demand so image rows can draw a preview.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<long, ClipPayload?>? PayloadLoader
    {
        get => _payloadLoader;
        set
        {
            _payloadLoader = value;
            ClearThumbnails();
        }
    }

    private Func<long, ClipPayload?>? _payloadLoader;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Ask Windows for the dark scrollbar so it does not glow white.
        Interop.NativeMethods.SetWindowTheme(Handle, "DarkMode_Explorer", null);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not ClipRow row)
        {
            return;
        }

        var graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, e.Bounds.Width - 8, e.Bounds.Height - 4);

        graphics.FillRectangle(_surfaceBrush, e.Bounds);

        if (selected)
        {
            using var highlight = Rounded(bounds, 6);
            graphics.FillPath(_selectionBrush, highlight);
        }

        var padding = 12;
        var textLeft = bounds.Left + padding;
        var textWidth = bounds.Width - (padding * 2);

        if (row.Clip.Kind == Core.Models.ClipKind.Image)
        {
            var box = new Rectangle(textLeft, bounds.Top + 6, bounds.Height - 12, bounds.Height - 12);

            graphics.FillRectangle(_raisedBrush, box);

            var thumbnail = GetThumbnail(row.Clip.Id);

            if (thumbnail is not null)
            {
                DrawThumbnail(graphics, thumbnail, box);
            }

            textLeft = box.Right + 12;
            textWidth = Math.Max(0, bounds.Right - padding - textLeft);
        }

        var titleRect = new Rectangle(textLeft, bounds.Top + 8, textWidth, 20);
        var metaRect = new Rectangle(textLeft, bounds.Top + 27, textWidth, 18);

        TextRenderer.DrawText(
            graphics,
            row.Title,
            Font,
            titleRect,
            Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);

        TextRenderer.DrawText(
            graphics,
            row.Meta,
            _metaFont ??= new Font(Font.FontFamily, Font.Size - 1.5f),
            metaRect,
            selected ? Theme.TextMuted : Theme.TextFaint,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);

        if (row.Clip.Pinned)
        {
            var dot = new Rectangle(bounds.Right - padding - 6, bounds.Top + 12, 5, 5);
            graphics.FillEllipse(_textBrush, dot);
        }
    }

    private Font? _metaFont;

    private static void DrawThumbnail(Graphics graphics, Bitmap thumbnail, Rectangle box)
    {
        var scale = Math.Min(box.Width / (float)thumbnail.Width, box.Height / (float)thumbnail.Height);
        var width = Math.Max(1, (int)Math.Round(thumbnail.Width * scale));
        var height = Math.Max(1, (int)Math.Round(thumbnail.Height * scale));
        var target = new Rectangle(
            box.Left + ((box.Width - width) / 2),
            box.Top + ((box.Height - height) / 2),
            width,
            height);

        using var path = Rounded(box, 5);
        var state = graphics.Save();

        try
        {
            graphics.SetClip(path);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(thumbnail, target);
        }
        finally
        {
            graphics.Restore(state);
        }

        using var border = new Pen(Theme.Border);
        graphics.DrawPath(border, path);
    }

    /// <summary>
    /// Decrypts and shrinks once per clip. Thumbnails stay small, so a long
    /// history cannot pin whole screenshots in memory.
    /// </summary>
    private Bitmap? GetThumbnail(long id)
    {
        if (_thumbnails.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (_thumbnails.Count >= ThumbnailCacheLimit)
        {
            ClearThumbnails();
        }

        Bitmap? thumbnail = null;
        var png = _payloadLoader?.Invoke(id)?.ImagePng;

        if (png is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(png);
                using var source = new Bitmap(stream);
                var scale = Math.Min(1f, 128f / Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));

                thumbnail = new Bitmap(width, height);

                using var graphics = Graphics.FromImage(thumbnail);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, width, height);
            }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
            {
                thumbnail = null;
            }
        }

        _thumbnails[id] = thumbnail;
        return thumbnail;
    }

    private void ClearThumbnails()
    {
        foreach (var bitmap in _thumbnails.Values)
        {
            bitmap?.Dispose();
        }

        _thumbnails.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metaFont?.Dispose();
            _surfaceBrush.Dispose();
            _selectionBrush.Dispose();
            _raisedBrush.Dispose();
            _textBrush.Dispose();
            ClearThumbnails();
        }

        base.Dispose(disposing);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
