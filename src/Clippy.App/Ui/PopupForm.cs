using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Clippy.App.Interop;
using Clippy.App.Services;
using Clippy.Core.Models;
using Clippy.Core.Search;
using Clippy.Core.Settings;
using Clippy.Core.Storage;

namespace Clippy.App.Ui;

/// <summary>
/// Borderless dark popup: search, browse, paste. The search box keeps focus the
/// whole time, so typing always filters and arrows always move the selection.
/// </summary>
internal sealed class PopupForm : Form
{
    private readonly ClipStore _store;
    private readonly CaptureService _capture;
    private readonly Func<ClippySettings> _settings;
    private readonly Action _openSettings;

    private readonly PictureBox _logo;
    private readonly Label _brand;
    private readonly IconGlyphButton _settingsButton;
    private readonly IconGlyphButton _closeButton;
    private readonly SearchField _search;
    private readonly ClipListBox _list;
    private readonly Label _empty;
    private readonly Label _footer;
    private readonly ContextMenuStrip _rowMenu;
    private readonly ToolStripMenuItem _pinItem;
    private readonly ToolStripMenuItem _nicknameItem;
    private readonly ToolStripMenuItem _deleteItem;

    private readonly List<StoredClip> _all = [];
    private IntPtr _targetWindow;
    private long _menuClipId;
    private bool _pasting;
    private Font? _brandFont;
    private Font? _bodyFont;
    private Font? _smallFont;
    private Font? _glyphFont;
    private Icon? _logoIcon;

    internal PopupForm(
        ClipStore store,
        CaptureService capture,
        Func<ClippySettings> settings,
        Action openSettings)
    {
        _store = store;
        _capture = capture;
        _settings = settings;
        _openSettings = openSettings;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Theme.Background;
        Padding = Padding.Empty;
        Opacity = 0;

        _logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        _brand = NewLabel("Clippy", Theme.Text, ContentAlignment.MiddleLeft);
        _settingsButton = new IconGlyphButton("\uE713", "Settings");
        _closeButton = new IconGlyphButton("\uE8BB", "Close");
        _search = new SearchField();
        _search.SetCue("Search clips");
        _list = new ClipListBox();
        _empty = NewLabel(string.Empty, Theme.TextFaint, ContentAlignment.MiddleCenter);
        _footer = NewLabel("Type to search   ↑↓   Enter paste   Ctrl+P pin   Ctrl+Del delete", Theme.TextFaint, ContentAlignment.MiddleCenter);

        Controls.AddRange([_logo, _brand, _settingsButton, _closeButton, _search, _list, _empty, _footer]);

        _settingsButton.Click += (_, _) => OnSettingsClicked();
        _closeButton.Click += (_, _) => Dismiss();

        // The header doubles as a title bar so the popup can be dragged.
        _brand.MouseDown += OnDragHandleMouseDown;
        _logo.MouseDown += OnDragHandleMouseDown;
        _footer.MouseDown += OnDragHandleMouseDown;
        _empty.MouseDown += OnDragHandleMouseDown;
        MouseDown += OnDragHandleMouseDown;

        _search.Box.TextChanged += (_, _) => ApplyFilter();
        _search.Box.KeyDown += OnSearchKeyDown;
        _list.MouseClick += OnListClick;
        _list.MouseUp += OnListMouseUp;

        _pinItem = new ToolStripMenuItem("Pin");
        _nicknameItem = new ToolStripMenuItem("Add nickname…");
        _deleteItem = new ToolStripMenuItem("Delete");

        _rowMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            Renderer = new DarkMenuRenderer(),
        };

        _pinItem.Click += (_, _) => TogglePinById(_menuClipId);
        _nicknameItem.Click += (_, _) => SetNicknameById(_menuClipId);
        _deleteItem.Click += (_, _) => DeleteById(_menuClipId);
        _rowMenu.Items.AddRange([_pinItem, _nicknameItem, _deleteItem]);
        _rowMenu.Closed += (_, _) =>
        {
            if (Visible)
            {
                Activate();
                _search.Box.Focus();
            }
        };

        _list.PayloadLoader = id => _store.LoadPayload(id);
    }

    internal bool IsPopupVisible => Visible;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    internal void ToggleAtCursor()
    {
        if (Visible)
        {
            Hide();
            return;
        }

        ShowAtCursor();
    }

    internal void ShowAtCursor()
    {
        if (Visible)
        {
            Activate();
            return;
        }

        // Remember where the user was before we stole focus.
        _targetWindow = ClipboardAccess.GetForegroundInfo().Handle;

        _search.Box.Clear();

        ReloadHistory();
        ApplyLayout();

        // Show first: handle creation re-runs layout at the real DPI, which can
        // grow the window. Placing before that would leave the footer off-screen.
        Show();
        Location = MonitorHelper.PlaceNearCursor(Cursor.Position, Size);
        ApplyRounding();
        Opacity = 1;
        Activate();
        _search.Box.Focus();

        // Set after Show so handle creation cannot reset it: newest clip wins.
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    internal void Dismiss()
    {
        if (!Visible)
        {
            return;
        }

        Hide();
    }

    private void OnDragHandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(
            Handle,
            NativeMethods.WM_NCLBUTTONDOWN,
            (IntPtr)NativeMethods.HTCAPTION,
            IntPtr.Zero);
    }

    private void OnSettingsClicked()
    {
        // The sheet opens modal over the popup; focus returns when it closes.
        _openSettings();

        if (Visible)
        {
            Activate();
            _search.Box.Focus();
        }
    }

    internal void ReloadHistory()
    {
        _all.Clear();
        _all.AddRange(_store.LoadAll(
            (id, error) => Log.Error($"Skipping unreadable clip {id}", error)));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _search.Box.Text;
        var candidates = _all.Select(static clip => clip.ToCandidate()).ToList();
        var ranked = ClipSearch.Rank(candidates, query);
        var byId = _all.ToDictionary(static clip => clip.Id);

        var previousId = (_list.SelectedItem as ClipRow)?.Clip.Id;

        _list.BeginUpdate();
        _list.Items.Clear();

        ClipRow? restore = null;

        foreach (var id in ranked)
        {
            if (!byId.TryGetValue(id, out var clip))
            {
                continue;
            }

            var row = new ClipRow
            {
                Clip = clip,
                KindLabel = ClipRow.KindLabelFor(clip),
                TimeLabel = RelativeTime.Format(clip.LastUsedAt),
            };

            _list.Items.Add(row);

            if (previousId == id)
            {
                restore = row;
            }
        }

        _list.EndUpdate();

        if (_list.Items.Count > 0)
        {
            _list.SelectedItem = restore ?? _list.Items[0];
        }

        var isEmpty = _list.Items.Count == 0;
        _list.Visible = !isEmpty;
        _empty.Visible = isEmpty;
        _empty.Text = string.IsNullOrWhiteSpace(query)
            ? "Nothing copied yet.\r\nCopy something and it shows up here."
            : "No clips match that search.";
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down:
                MoveSelection(1);
                break;

            case Keys.Up:
                MoveSelection(-1);
                break;

            case Keys.PageDown:
                MoveSelection(5);
                break;

            case Keys.PageUp:
                MoveSelection(-5);
                break;

            case Keys.Enter:
                PasteSelected(plainText: e.Shift);
                break;

            default:
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);

        if (next != _list.SelectedIndex)
        {
            _list.SelectedIndex = next;
        }
    }

    private async void PasteSelected(bool plainText)
    {
        if (_pasting || _list.SelectedItem is not ClipRow row)
        {
            return;
        }

        var payload = _store.LoadPayload(row.Clip.Id);

        if (payload is null)
        {
            return;
        }

        _pasting = true;

        try
        {
            _store.Touch(row.Clip.Id);

            var target = _targetWindow;
            var shouldPaste = _settings().PasteOnEnter;

            // Our own clipboard write must not be re-captured as a new clip.
            _capture.SuppressCaptureFor(TimeSpan.FromMilliseconds(900));

            try
            {
                ClipboardAccess.Write(payload, plainText);
            }
            catch (ExternalException ex)
            {
                Log.Error("Failed to write clip back to the clipboard", ex);
                return;
            }

            if (shouldPaste)
            {
                // The injector waits on the target window and for the clipboard
                // to be read. Keep that wait off the UI thread so the popup does
                // not freeze mid-paste.
                await Task.Run(() => PasteInjector.TryPaste(target, Handle));
            }

            // The popup deliberately stays open: focus comes back here so the
            // next pick, Esc and filtering keep working.
            if (Visible)
            {
                Activate();
                _search.Box.Focus();
            }
        }
        finally
        {
            _pasting = false;
        }
    }

    private void OnListClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var index = _list.IndexFromPoint(e.Location);

        if (index < 0 || index >= _list.Items.Count)
        {
            return;
        }

        _list.SelectedIndex = index;
        PasteSelected(plainText: Control.ModifierKeys.HasFlag(Keys.Shift));
    }

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var index = _list.IndexFromPoint(e.Location);

        if (index < 0 || index >= _list.Items.Count || _list.Items[index] is not ClipRow row)
        {
            return;
        }

        _list.SelectedIndex = index;
        _menuClipId = row.Clip.Id;
        _pinItem.Text = row.Clip.Pinned ? "Unpin" : "Pin";
        _nicknameItem.Visible = row.Clip.Kind == ClipKind.Text;
        _nicknameItem.Text = string.IsNullOrWhiteSpace(row.Clip.Nickname)
            ? "Add nickname…"
            : "Change nickname…";
        _rowMenu.Show(_list, e.Location);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                Dismiss();
                return true;

            case Keys.Control | Keys.P:
                TogglePin();
                return true;

            case Keys.Control | Keys.Delete:
                DeleteSelected();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void TogglePin()
    {
        if (_list.SelectedItem is ClipRow row)
        {
            TogglePinById(row.Clip.Id);
        }
    }

    private void TogglePinById(long id)
    {
        var clip = _all.FirstOrDefault(candidate => candidate.Id == id);

        if (clip is null)
        {
            return;
        }

        _store.SetPinned(id, !clip.Pinned);
        ReloadHistory();
        SelectById(id);
        Log.Info($"Clip {id} pinned={!clip.Pinned}");
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItem is ClipRow row)
        {
            DeleteById(row.Clip.Id);
        }
    }

    private void SetNicknameById(long id)
    {
        var clip = _all.FirstOrDefault(candidate => candidate.Id == id);

        if (clip is null || clip.Kind != ClipKind.Text)
        {
            return;
        }

        using var dialog = new NicknameDialog(clip.Nickname)
        {
            TopMost = TopMost,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _store.SetNickname(id, dialog.Nickname);
        ReloadHistory();
        SelectById(id);
        Log.Info(string.IsNullOrEmpty(dialog.Nickname)
            ? $"Clip {id} nickname cleared"
            : $"Clip {id} nickname set");
    }

    private void DeleteById(long id)
    {
        var index = _list.SelectedIndex;
        _store.Delete(id);
        _all.RemoveAll(clip => clip.Id == id);
        ApplyFilter();

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        }
    }

    private void SelectById(long id)
    {
        foreach (var item in _list.Items)
        {
            if (item is ClipRow candidate && candidate.Clip.Id == id)
            {
                _list.SelectedItem = candidate;
                return;
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRounding();
        ApplyFonts();
        ApplyLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyFonts();
        ApplyLayout();

        // Moving to a monitor with a different scale changes the window size, so
        // keep the whole popup inside the new work area.
        if (Visible)
        {
            Location = MonitorHelper.PlaceNearCursor(Cursor.Position, Size);
            ApplyRounding();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        // The popup stays open until Win+V or Esc. Remember whichever window
        // took focus, so the next pick pastes where the user actually is.
        var source = ClipboardAccess.GetForegroundInfo();

        if (source.Handle != IntPtr.Zero && !string.Equals(source.ProcessName, "Clippy", StringComparison.OrdinalIgnoreCase))
        {
            _targetWindow = source.Handle;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Dismiss();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private float DpiScale => DeviceDpi / 96f;

    private int S(int logical) => (int)Math.Round(logical * DpiScale);

    private static Label NewLabel(string text, Color color, ContentAlignment alignment) => new()
    {
        Text = text,
        AutoSize = false,
        ForeColor = color,
        BackColor = Color.Transparent,
        TextAlign = alignment,
        UseCompatibleTextRendering = false,
    };

    private void ApplyFonts()
    {
        var scale = DpiScale;

        _brandFont?.Dispose();
        _bodyFont?.Dispose();
        _smallFont?.Dispose();
        _glyphFont?.Dispose();
        _logoIcon?.Dispose();

        _brandFont = new Font(Theme.FontFamily, 10f * scale, FontStyle.Bold, GraphicsUnit.Point);
        _bodyFont = new Font(Theme.FontFamily, 9.5f * scale, FontStyle.Regular, GraphicsUnit.Point);
        _smallFont = new Font(Theme.FontFamily, 7.75f * scale, FontStyle.Regular, GraphicsUnit.Point);
        _glyphFont = new Font("Segoe MDL2 Assets", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);

        _brand.Font = _brandFont;
        _settingsButton.GlyphFont = _glyphFont;
        _closeButton.GlyphFont = _glyphFont;
        _search.Box.Font = _bodyFont;
        _list.Font = _bodyFont;
        _empty.Font = _bodyFont;
        _footer.Font = _smallFont;

        _logoIcon?.Dispose();
        _logoIcon = AppIconFactory.Create(Color.White, 64);
        _logo.Image?.Dispose();
        _logo.Image = _logoIcon.ToBitmap();
    }

    private void ApplyLayout()
    {
        var padding = S(Theme.BasePadding);
        var width = S(Theme.BaseWidth);
        var height = S(Theme.BaseHeight);
        var contentWidth = width - (padding * 2);

        ClientSize = new Size(width, height);

        var y = padding;
        var logoSize = S(20);

        _logo.SetBounds(padding, y + S(3), logoSize, logoSize);

        var buttonSize = S(26);
        var closeLeft = width - padding - buttonSize;
        var settingsLeft = closeLeft - S(4) - buttonSize;

        _closeButton.SetBounds(closeLeft, y + S(2), buttonSize, buttonSize);
        _settingsButton.SetBounds(settingsLeft, y + S(2), buttonSize, buttonSize);

        var brandLeft = padding + logoSize + S(8);
        _brand.SetBounds(
            brandLeft,
            y,
            Math.Max(0, settingsLeft - S(8) - brandLeft),
            S(Theme.BaseHeaderHeight));

        y += S(Theme.BaseHeaderHeight) + S(10);

        _search.SetBounds(padding, y, contentWidth, S(Theme.BaseSearchHeight));
        _search.SetCue("Search clips");

        y += S(Theme.BaseSearchHeight) + S(10);

        var footerHeight = S(Theme.BaseFooterHeight);

        _list.SetBounds(padding, y, contentWidth, height - y - footerHeight - padding);
        _empty.SetBounds(padding, y, contentWidth, height - y - footerHeight - padding);
        _footer.SetBounds(padding, height - padding - footerHeight + S(6), contentWidth, footerHeight);

        _list.ItemHeight = S(Theme.BaseRowHeight);
    }

    private void ApplyRounding()
    {
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var preference = NativeMethods.DWMWCP_ROUND;
            var result = NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));

            if (result == 0)
            {
                return;
            }
        }

        var radius = S(8);
        var diameter = radius * 2;
        var rect = new Rectangle(0, 0, Width, Height);

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        Region = new Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _brandFont?.Dispose();
            _bodyFont?.Dispose();
            _smallFont?.Dispose();
            _glyphFont?.Dispose();
            _logoIcon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
