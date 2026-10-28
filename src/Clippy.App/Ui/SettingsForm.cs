using System.Drawing;
using System.Windows.Forms;
using Clippy.App.Interop;
using Clippy.Core.Settings;
using Clippy.Core.Storage;

namespace Clippy.App.Ui;

/// <summary>Dark settings sheet. Applies to the live settings object on save.</summary>
internal sealed class SettingsForm : Form
{
    private readonly ClipStore _store;
    private readonly ClippySettings _settings;
    private readonly Action _persist;

    private readonly Label _heading;
    private readonly Label _maxEntriesLabel;
    private readonly Label _retentionLabel;
    private readonly Label _storageLabel;
    private readonly Label _excludedLabel;
    private readonly Label _hotkeyLabel;
    private readonly Label _hotkeyHint;
    private readonly NumericUpDown _maxEntries;
    private readonly NumericUpDown _retentionDays;
    private readonly NumericUpDown _maxStorage;
    private readonly CheckBox _captureEnabled;
    private readonly CheckBox _launchAtLogin;
    private readonly CheckBox _pasteOnEnter;
    private readonly HotkeyBox _hotkey;
    private readonly TextBox _excluded;
    private readonly Label _usage;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Button _clear;

    internal SettingsForm(ClipStore store, ClippySettings settings, Action persist)
    {
        _store = store;
        _settings = settings;
        _persist = persist;

        Text = "Clippy settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 704);
        Font = new Font(Theme.FontFamily, 9f);

        _heading = new Label
        {
            Text = "Settings",
            AutoSize = false,
            Font = new Font(Theme.FontFamily, 12f, FontStyle.Bold),
            ForeColor = Theme.Text,
        };

        _maxEntriesLabel = FieldLabel("Clips kept");
        _retentionLabel = FieldLabel("Expire after (days)");
        _storageLabel = FieldLabel("Storage cap (MB)");
        _excludedLabel = FieldLabel("Never capture from these apps (one process name per line)");
        _hotkeyLabel = FieldLabel("Global hotkey");
        _hotkeyHint = new Label
        {
            AutoSize = false,
            ForeColor = Theme.TextFaint,
            Text = "Click the box, then press the keys. Backspace resets to Win+V.",
        };

        _maxEntries = Number(settings.MaxEntries, 10, 10_000, 10);
        _retentionDays = Number(settings.RetentionDays, 1, 3650, 1);
        _maxStorage = Number(settings.MaxStorageMb, 16, 8192, 16);

        _captureEnabled = Check("Capture copies", settings.CaptureEnabled);
        _launchAtLogin = Check("Start with Windows", settings.LaunchAtLogin);
        _pasteOnEnter = Check("Paste automatically after choosing a clip", settings.PasteOnEnter);
        _hotkey = new HotkeyBox(settings.Hotkey);
        _hotkey.Rejected += message =>
        {
            _hotkeyHint.ForeColor = Color.FromArgb(0xFF, 0x8A, 0x8A);
            _hotkeyHint.Text = message;
        };
        _hotkey.Accepted += () =>
        {
            _hotkeyHint.ForeColor = Theme.TextFaint;
            _hotkeyHint.Text = "Click the box, then press the keys. Backspace resets to Win+V.";
        };

        _excluded = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(Theme.FontFamily, 9f),
            Text = string.Join(Environment.NewLine, settings.ExcludedProcesses),
        };

        _usage = new Label
        {
            AutoSize = false,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = DescribeUsage(),
        };

        _save = ActionButton("Save");
        _save.Click += (_, _) => Apply();

        _cancel = ActionButton("Cancel");
        _cancel.Click += (_, _) => Close();

        _clear = ActionButton("Clear history");
        _clear.ForeColor = Color.FromArgb(0xFF, 0x8A, 0x8A);
        _clear.Click += (_, _) => ClearHistory();

        CancelButton = _cancel;

        Controls.AddRange(
        [
            _heading,
            _maxEntriesLabel, _maxEntries,
            _retentionLabel, _retentionDays,
            _storageLabel, _maxStorage,
            _captureEnabled, _launchAtLogin, _pasteOnEnter,
            _hotkeyLabel, _hotkey, _hotkeyHint,
            _excludedLabel, _excluded,
            _usage,
            _clear, _cancel, _save,
        ]);

        LayoutEverything();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Dark caption bar instead of the default light one.
        var enabled = 1;

        if (NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref enabled,
                sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                ref enabled,
                sizeof(int));
        }

        NativeMethods.SetWindowTheme(_excluded.Handle, "DarkMode_Explorer", null);
    }

    private void LayoutEverything()
    {
        var left = 20;
        var width = ClientSize.Width - (left * 2);
        var y = 16;

        _heading.SetBounds(left, y, width, 30);
        y += 40;

        PlaceNumber(_maxEntriesLabel, _maxEntries, ref y, left, width);
        PlaceNumber(_retentionLabel, _retentionDays, ref y, left, width);
        PlaceNumber(_storageLabel, _maxStorage, ref y, left, width);

        _captureEnabled.SetBounds(left, y, width, 22);
        y += 28;
        _launchAtLogin.SetBounds(left, y, width, 22);
        y += 28;
        _pasteOnEnter.SetBounds(left, y, width, 22);
        y += 34;

        _hotkeyLabel.SetBounds(left, y, width, 18);
        y += 20;
        _hotkey.SetBounds(left, y, 220, 26);
        y += 30;
        _hotkeyHint.SetBounds(left, y, width, 18);
        y += 28;

        _excludedLabel.SetBounds(left, y, width, 18);
        y += 22;
        _excluded.SetBounds(left, y, width, 130);
        y += 140;

        _usage.SetBounds(left, y, width, 20);
        y += 30;

        _clear.SetBounds(left, y, 120, 32);
        _save.SetBounds(ClientSize.Width - left - 90, y, 90, 32);
        _cancel.SetBounds(ClientSize.Width - left - 190, y, 90, 32);
    }

    private static void PlaceNumber(Label label, NumericUpDown input, ref int y, int left, int width)
    {
        label.SetBounds(left, y, width, 18);
        y += 20;
        input.SetBounds(left, y, 140, 24);
        y += 34;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        ForeColor = Theme.TextMuted,
    };

    private static NumericUpDown Number(int value, int min, int max, int step) => new DarkNumericUpDown
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Increment = step,
    };

    private static CheckBox Check(string text, bool value) => new DarkCheckBox
    {
        Text = text,
        Checked = value,
    };

    private static Button ActionButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
        };

        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private void Apply()
    {
        _settings.MaxEntries = (int)_maxEntries.Value;
        _settings.RetentionDays = (int)_retentionDays.Value;
        _settings.MaxStorageMb = (int)_maxStorage.Value;
        _settings.CaptureEnabled = _captureEnabled.Checked;
        _settings.LaunchAtLogin = _launchAtLogin.Checked;
        _settings.PasteOnEnter = _pasteOnEnter.Checked;
        _settings.HotkeyModifiers = _hotkey.Binding.Modifiers;
        _settings.HotkeyKey = _hotkey.Binding.VirtualKey;
        _settings.ExcludedProcesses = _excluded.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();

        _persist();
        Close();
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(
            this,
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
        _usage.Text = DescribeUsage();
        Services.Log.Info("History cleared from settings (pinned kept)");
    }

    private string DescribeUsage()
    {
        var (count, bytes) = _store.GetUsage();
        return $"{count} clips · {bytes / (1024.0 * 1024.0):0.#} MB on disk";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font?.Dispose();
        }

        base.Dispose(disposing);
    }
}
