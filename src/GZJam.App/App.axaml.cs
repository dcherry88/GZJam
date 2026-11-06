using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GZJam.App.ViewModels;
using GZJam.App.Views;
using GZJam.Audio;
using GZJam.Library;

namespace GZJam.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Start(AppPaths.DataDirectory);
            Log.Info($"GZJam starting on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved task exception", e.Exception);
            Dispatcher.UIThread.UnhandledException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);

            RestoreIfAsked();   // before the settings are read: a backup brings its own
            var settings = AppSettings.Load(AppPaths.SettingsFile);
            StartOverIfAsked(settings);
            if (LibraryDatabase.CheckCanOpen(AppPaths.LibraryDatabase) is { } tooNew)
            {
                ExplainLibraryTooNew(desktop, tooNew);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (StartupWindow.IsShown)
            {
                var startup = new StartupWindow(settings.Window);
                startup.Show();
                _ = StartBehindAsync(desktop, settings, startup);
            }
            else
            {
                var viewModel = CreateViewModel(settings);
                CreateMainWindow(desktop, settings, viewModel);
                viewModel.Start();
                AfterStart(viewModel);
                viewModel.LatestRelease = UpdateCheck.LatestAsync;
                _ = viewModel.CheckForNewVersionAsync();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Starting over with an empty library, if the last GZJam asked for it (see <see cref="LibraryReset"/>).</summary>
    private static void StartOverIfAsked(AppSettings settings)
    {
        try
        {
            if (LibraryReset.ApplyPending(AppPaths.LibraryDatabase) is not { } kept)
                return;
            Log.Info($"Started over with an empty library; the old one is kept in {kept}");
            settings.SelectedRootPath = null;
            settings.SelectedFolder = null;
            settings.OpenPlaylist = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't start over with an empty library", ex);
        }
    }

    /// <summary>The backup restored as this GZJam started, if one was, to say so once it's open.</summary>
    private static BackupInfo? s_restored;

    /// <summary>Restoring a backup, if the last GZJam readied one (see <see cref="LibraryBackup"/>).</summary>
    private static void RestoreIfAsked()
    {
        try
        {
            if (LibraryBackup.ApplyPending(AppPaths.LibraryDatabase, AppPaths.SettingsFile) is not { } restore)
                return;
            s_restored = restore.Restored;
            Log.Info($"Restored the backup from {restore.Restored.Created:u} (GZJam {restore.Restored.GZJamVersion}); " +
                     $"the library it replaced is kept in {restore.Kept}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Error("Couldn't restore the backup", ex);
        }
    }

    /// <summary>Once the main window is up: the automatic backup, and saying a backup was restored.</summary>
    private static void AfterStart(MainWindowViewModel viewModel)
    {
        viewModel.KeepsAutomaticBackups = true;
        _ = ServerLibrary.SyncQuietlyAsync(viewModel);   // the server's songs, if one is set up
        if (s_restored is { } info)
            viewModel.ShowNotice($"The backup from {info.Created:d MMMM yyyy} is restored."
                + (info.Moved is { Count: > 0 } moved ? $" Its music is in {string.Join(", ", moved)}." : ""), seconds: 15);
    }

    /// <summary>Opens GZJam again, once this one has closed (to start over, or restore a backup).</summary>
    private static void Relaunch(bool startOver)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // let go of the library before the next one moves it
            if (startOver)
                LibraryReset.Request(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            Log.Info(startOver ? "Opening GZJam again, to start over with an empty library" : "Opening GZJam again, to restore a backup");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Couldn't open GZJam again", ex);
        }
    }

    /// <summary>How long the startup screen stays once everything is ready, for its sweep to reach the end.</summary>
    private static readonly TimeSpan StartupFinish = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets GZJam ready behind the startup screen a step at a time, letting it draw a frame between
    /// steps so its sweep follows along. The main window opens underneath it; once that's drawn and
    /// the outputs are open, the startup screen goes.
    /// </summary>
    private static async Task StartBehindAsync(IClassicDesktopStyleApplicationLifetime desktop, AppSettings settings, StartupWindow startup)
    {
        try
        {
            startup.BeginStep(0, 0.2);
            await NextFrame(startup);   // the startup screen is on screen
            Log.Info($"Startup screen up, {SinceStart:0.00} s after starting");
            var viewModel = CreateViewModel(settings);

            startup.BeginStep(0.2, 0.6);
            await NextFrame(startup);
            var window = CreateMainWindow(desktop, settings, viewModel);
            window.Show();

            startup.BeginStep(0.6, 0.85);
            await NextFrame(window);   // laid out and drawn, out of sight
            viewModel.Start();   // opens the outputs
            AfterStart(viewModel);
            viewModel.LatestRelease = UpdateCheck.LatestAsync;
            _ = viewModel.CheckForNewVersionAsync();   // in the background

            startup.Finish();
            await Task.Delay(StartupFinish);
            startup.Dismiss();
            Log.Info($"Ready, {SinceStart:0.00} s after starting");
        }
        catch (Exception ex)
        {
            // As before there was a startup screen: logged, and GZJam stops.
            Log.Error("GZJam couldn't start", ex);
            startup.Dismiss();
            var failure = ExceptionDispatchInfo.Capture(ex);
            Dispatcher.UIThread.Post(failure.Throw);
        }
    }

    /// <summary>
    /// The library was upgraded by a newer GZJam, in a way this one can't read (after going back
    /// from a newer release or a test build). Says so, rather than closing without a word, and
    /// offers the newest GZJam. Nothing is opened or changed.
    /// </summary>
    private static void ExplainLibraryTooNew(IClassicDesktopStyleApplicationLifetime desktop, LibraryTooNewException tooNew)
    {
        Log.Error($"Not opening {AppPaths.LibraryDatabase}: {tooNew.Message}");
        var dialog = LibraryTooNewDialog();
        dialog.Closed += (_, _) =>
        {
            if (dialog.Confirmed)
                OpenInBrowser(About.ProjectUrl + "/releases/latest");
            desktop.Shutdown();
        };
        desktop.MainWindow = dialog;
        dialog.Show();
    }

    /// <summary>What's shown when the library is from a newer GZJam.</summary>
    internal static ConfirmDialog LibraryTooNewDialog()
    {
        var dialog = new ConfirmDialog(
            "Your library is from a newer GZJam",
            $"A newer version of GZJam has already updated your library, and this version ({About.Version}) can't read it. " +
            "Install the newest GZJam to carry on.\n\n" +
            "Nothing has been changed or lost: your music, History, playlists and marks are all still there for the newer version.",
            "Get the newest GZJam", "Quit")
        {
            Title = "GZJam",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        dialog.ConfirmIsHarmless();
        return dialog;
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Couldn't open {url}", ex);
        }
    }

    /// <summary>Seconds since GZJam was started, for the log.</summary>
    private static double SinceStart => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;

    /// <summary>Completes once <paramref name="window"/> has drawn its next frame.</summary>
    private static Task NextFrame(TopLevel window)
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => drawn.TrySetResult());
        return drawn.Task;
    }

    /// <summary>Opens the library and starts audio.</summary>
    private static MainWindowViewModel CreateViewModel(AppSettings settings)
    {
        ServerConnections.Configure();
        var database = new LibraryDatabase(AppPaths.LibraryDatabase);
        var (audio, mix, audioError) = StartAudio();
        return new MainWindowViewModel(database, settings, audio, mix, audioError);
    }

    private static MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop, AppSettings settings, MainWindowViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel };
        window.RestorePlacement(settings.Window);
        // The room's song from outside the window (#262): Linux desktops have no such place.
        if (OperatingSystem.IsWindows())
            TaskbarButtons.Attach(window, viewModel.Room);
        else if (OperatingSystem.IsMacOS())
            DockMenu.Attach(window, viewModel.Room);
        bool? startOver = null;   // set: open again afterward (true: starting over; false: restoring)
        viewModel.StartOverRequested += () =>
        {
            startOver = true;
            window.Close();   // the room hears nothing (Settings checks), so it closes without asking
        };
        viewModel.RestoreRequested += () =>
        {
            startOver = false;
            window.Close();
        };
        window.Opened += (_, _) =>
            Log.Info($"Window open, {SinceStart:0.00} s after starting");
        window.Closing += (_, _) =>
        {
            settings.Window = window.CapturePlacement();
            viewModel.SaveState();
            settings.Save(AppPaths.SettingsFile);
        };
        desktop.Exit += (_, _) =>
        {
            Log.Info("GZJam closing");
            viewModel.CloseNight();
            viewModel.Dispose();
            RemoteSongs.ClearTemporary();   // songs fetched only to play; one a deck still has open goes at the next start
            if (startOver is { } reset)
                Relaunch(reset);
        };
        desktop.MainWindow = window;
        return window;
    }

    /// <summary>Loads BASS. If that fails the library still works; the window explains why there's no sound.</summary>
    private static (AudioEngine?, MixEngine?, string?) StartAudio()
    {
        AudioEngine? audio = null;
        try
        {
            audio = new AudioEngine();
            Log.Info($"BASS {audio.BassVersion}, BASS_FX {audio.FxVersion}, BASSmix {audio.MixVersion}, formats: {string.Join(", ", AudioEngine.LoadedAddOns)}");
            foreach (AudioDevice device in audio.GetOutputDevices())
                Log.Info($"  output {device.Index}: {device} [{device.Driver}]" + (device.SystemName != device.Name ? $", called \"{device.SystemName}\" by the system" : ""));
            return (audio, new MixEngine(audio), null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or AudioException)
        {
            audio?.Dispose();
            Log.Error("Audio failed to start", ex);
            return (null, null, $"Audio isn't available: {ex.Message}");
        }
    }
}
