using System.Text.Json;
using System.Text.Json.Serialization;
using Clippy.Core.Hotkey;
using Clippy.Core.Storage;

namespace Clippy.Core.Settings;

public sealed class ClippySettings
{
    public static readonly string[] DefaultExcludedProcesses =
    [
        "KeePass",
        "KeePassXC",
        "1Password",
        "Bitwarden",
        "Keeper",
        "mstsc",
    ];

    public int MaxEntries { get; set; } = 500;

    public int RetentionDays { get; set; } = 30;

    public int MaxStorageMb { get; set; } = 512;

    public bool CaptureEnabled { get; set; } = true;

    public bool LaunchAtLogin { get; set; } = true;

    public bool PasteOnEnter { get; set; } = true;

    /// <summary>Set once the user has decided how to handle the Win+V conflict.</summary>
    public bool WinVPromptDismissed { get; set; }

    /// <summary>Modifier flags for the global shortcut. Default is Win+V.</summary>
    public uint HotkeyModifiers { get; set; } = HotkeyBinding.Default.Modifiers;

    /// <summary>Virtual key code for the global shortcut. Default is V.</summary>
    public int HotkeyKey { get; set; } = HotkeyBinding.DefaultKey;

    public List<string> ExcludedProcesses { get; set; } = [.. DefaultExcludedProcesses];

    /// <summary>The stored hotkey, falling back to Win+V when the file is invalid.</summary>
    [JsonIgnore]
    public HotkeyBinding Hotkey =>
        HotkeyBinding.TryCreate(HotkeyModifiers, HotkeyKey, out var binding, out _)
            ? binding
            : HotkeyBinding.Default;

    public RetentionPolicy ToRetentionPolicy() =>
        new(Math.Max(1, MaxEntries), Math.Max(1, RetentionDays), Math.Max(1, MaxStorageMb) * 1024L * 1024L);

    public static ClippySettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ClippySettings>(json);

                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return new ClippySettings();
    }

    public void Save(string path)
    {
        Normalize();

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private void Normalize()
    {
        MaxEntries = Math.Clamp(MaxEntries, 10, 10_000);
        RetentionDays = Math.Clamp(RetentionDays, 1, 3650);
        MaxStorageMb = Math.Clamp(MaxStorageMb, 16, 8192);
        ExcludedProcesses ??= [];
        ExcludedProcesses = ExcludedProcesses
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!HotkeyBinding.TryCreate(HotkeyModifiers, HotkeyKey, out var hotkey, out _))
        {
            hotkey = HotkeyBinding.Default;
        }

        HotkeyModifiers = hotkey.Modifiers;
        HotkeyKey = hotkey.VirtualKey;
    }
}
