using Clippy.App.Services;
using Clippy.Core;
using Clippy.Core.Storage;

namespace Clippy.App;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var instance = SingleInstance.TryAcquire();

        if (instance is null)
        {
            // Another Clippy owns the session; ask it to show the popup.
            SingleInstance.SignalRunningInstance(ParseStartupMode());
            return 0;
        }

        using (instance)
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFatal(e.Exception, "UI thread");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "background thread");

            Directory.CreateDirectory(ClippyPaths.Root);

            Log.Info("Clippy starting");

            try
            {
                using var host = new AppHost(instance, ParseStartupMode());
                Application.Run(host);
            }
            catch (ClipStoreKeyException ex)
            {
                if (!ConfirmResetHistory(ex))
                {
                    return 1;
                }

                try
                {
                    using var host = new AppHost(instance, ParseStartupMode());
                    Application.Run(host);
                }
                catch (Exception retryError)
                {
                    ReportFatal(retryError, "startup");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                ReportFatal(ex, "startup");
                return 1;
            }

            Log.Info("Clippy stopped");
        }

        return 0;
    }

    private static StartupMode ParseStartupMode()
    {
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            if (string.Equals(argument, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                return StartupMode.Settings;
            }

            if (string.Equals(argument, "--open", StringComparison.OrdinalIgnoreCase))
            {
                return StartupMode.Popup;
            }
        }

        return StartupMode.None;
    }

    private static bool ConfirmResetHistory(ClipStoreKeyException exception)
    {
        Log.Error("History key mismatch", exception);

        var answer = MessageBox.Show(
            "Clippy cannot decrypt your saved history.\n\n" +
            "The encryption key was replaced or belongs to another Windows account. " +
            "The old file is kept on disk for recovery.\n\n" +
            "Start with an empty history?",
            "Clippy",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return false;
        }

        try
        {
            foreach (var moved in ClipStore.MoveAside(exception.DatabasePath))
            {
                Log.Info($"Old history moved aside: {moved}");
            }
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            ReportFatal(moveError, "history recovery");
            return false;
        }

        return true;
    }

    private static void ReportFatal(Exception? exception, string origin)
    {
        Log.Error($"Fatal error on {origin}", exception);

        if (exception is null)
        {
            return;
        }

        MessageBox.Show(
            $"Clippy hit an unrecoverable error.\n\n{exception.GetType().Name}: {exception.Message}\n\nDetails were written to:\n{ClippyPaths.Logs}",
            "Clippy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
