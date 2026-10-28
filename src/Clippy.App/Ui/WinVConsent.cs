using Clippy.App.Services;

namespace Clippy.App.Ui;

/// <summary>
/// One-time question shown when the shell refuses to give up Win+V. Answering
/// "yes" disables Windows clipboard history; "no" keeps the keyboard hook and
/// stops the question from returning.
/// </summary>
internal static class WinVConsent
{
    internal static bool ShouldAsk(bool alreadyDismissed)
    {
        return !alreadyDismissed && WindowsClipboardPolicy.IsHistoryEnabled();
    }

    /// <summary>Returns <c>true</c> when Windows history was turned off.</summary>
    internal static bool AskAndApply()
    {
        const string message =
            "Windows is using Win+V for its own clipboard history, so Clippy is " +
            "listening through a keyboard hook instead.\n\n" +
            "Turn off Windows clipboard history so Clippy can own Win+V directly?\n\n" +
            "This also turns off cloud clipboard sync. Win+. (emoji) and Win+; (symbols) " +
            "are not affected.";

        var answer = MessageBox.Show(
            message,
            "Clippy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (answer != DialogResult.Yes)
        {
            return false;
        }

        WindowsClipboardPolicy.DisableHistory();
        return true;
    }
}
