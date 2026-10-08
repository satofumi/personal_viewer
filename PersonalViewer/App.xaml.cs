using System.IO;
using System.Windows;
using PersonalViewer.Configuration;
using PersonalViewer.Diagnostics;
using PersonalViewer.Localization;
using System.Windows.Threading;

namespace PersonalViewer;

public partial class App : Application
{
    public AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogger.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LocalizationService.Configure("auto");

        try
        {
            Settings = SettingsStore.LoadOrCreate();
            LocalizationService.Configure(Settings.Language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            AppLogger.Error("settings-load", exception, $"path={SettingsStore.SettingsFilePath}");
            MessageBox.Show(
                LocalizationService.Format(
                    "SettingsLoadErrorDetails",
                    SettingsStore.SettingsFilePath,
                    exception.Message),
                LocalizationService.GetString("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("shutdown", $"exitCode={e.ApplicationExitCode}");
        AppLogger.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Critical("dispatcher-unhandled", e.Exception, $"handled={e.Handled}");
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogger.Critical("appdomain-unhandled", exception, $"terminating={e.IsTerminating}");
        }
        else
        {
            AppLogger.Info("appdomain-unhandled", $"terminating={e.IsTerminating}; exception={e.ExceptionObject}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("task-unobserved", e.Exception);
    }
}
