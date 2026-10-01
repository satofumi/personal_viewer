using System.IO;
using System.Windows;
using PersonalViewer.Configuration;
using PersonalViewer.Localization;

namespace PersonalViewer;

public partial class App : Application
{
    public AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LocalizationService.Configure("auto");

        try
        {
            Settings = SettingsStore.LoadOrCreate();
            LocalizationService.Configure(Settings.Language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
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
}
