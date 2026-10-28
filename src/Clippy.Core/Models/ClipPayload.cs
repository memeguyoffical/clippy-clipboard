namespace Clippy.Core.Models;

public enum ClipKind
{
    Text = 0,
    RichText = 1,
    Html = 2,
    Image = 3,
    Files = 4,
}

/// <summary>
/// Everything captured from one copy operation. A single clip can carry several
/// formats at once (a browser copy usually yields text + HTML), which is what
/// lets rich paste stay faithful.
/// </summary>
public sealed class ClipPayload
{
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public byte[]? ImagePng { get; init; }
    public string[]? Files { get; init; }
    public string? SourceApp { get; init; }
    public string? SourceTitle { get; init; }

    public ClipKind Kind
    {
        get
        {
            if (Files is { Length: > 0 })
            {
                return ClipKind.Files;
            }

            if (ImagePng is { Length: > 0 })
            {
                return ClipKind.Image;
            }

            if (!string.IsNullOrEmpty(Html))
            {
                return ClipKind.Html;
            }

            if (!string.IsNullOrEmpty(Rtf))
            {
                return ClipKind.RichText;
            }

            return ClipKind.Text;
        }
    }

    public bool IsEmpty =>
        string.IsNullOrEmpty(Text)
        && string.IsNullOrEmpty(Html)
        && string.IsNullOrEmpty(Rtf)
        && ImagePng is not { Length: > 0 }
        && Files is not { Length: > 0 };

    /// <summary>Rough byte cost, used for storage-cap pruning.</summary>
    public long EstimatedBytes =>
        (Text?.Length ?? 0) * 2L
        + (Html?.Length ?? 0) * 2L
        + (Rtf?.Length ?? 0) * 2L
        + (ImagePng?.LongLength ?? 0)
        + (Files?.Sum(f => f.Length * 2L) ?? 0L);

    /// <summary>Plain text used for search and for the list preview.</summary>
    public string SearchText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                return Text;
            }

            if (!string.IsNullOrWhiteSpace(Html))
            {
                return Clippy.Core.Text.PlainText.FromHtml(Html);
            }

            if (!string.IsNullOrWhiteSpace(Rtf))
            {
                return Clippy.Core.Text.PlainText.FromRtf(Rtf);
            }

            if (Files is { Length: > 0 })
            {
                return string.Join(Environment.NewLine, Files);
            }

            return string.Empty;
        }
    }
}
