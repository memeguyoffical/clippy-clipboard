namespace Clippy.App.Ui;

internal static class RelativeTime
{
    internal static string Format(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;

        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        if (age.TotalSeconds < 45)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h";
        }

        if (age.TotalDays < 7)
        {
            return $"{(int)age.TotalDays}d";
        }

        return value.ToLocalTime().ToString("d MMM");
    }
}
