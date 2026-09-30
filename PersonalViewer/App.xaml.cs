using System.IO;
using System.Windows;
using PersonalViewer.Configuration;

namespace PersonalViewer;

public partial class App : Application
{
    public AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Settings = SettingsStore.LoadOrCreate();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            MessageBox.Show(
                $"アプリ設定を読み込めませんでした。{Environment.NewLine}{SettingsStore.SettingsFilePath}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
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
