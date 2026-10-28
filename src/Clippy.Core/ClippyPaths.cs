namespace Clippy.Core;

/// <summary>Single place that decides where Clippy keeps its files.</summary>
public static class ClippyPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clippy");

    public static string Database => Path.Combine(Root, "clippy.db");

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Logs => Path.Combine(Root, "logs");
}
