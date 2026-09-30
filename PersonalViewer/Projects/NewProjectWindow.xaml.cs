using System.IO;
using System.Windows;
using Microsoft.Win32;
using PersonalViewer.Configuration;

namespace PersonalViewer.Projects;

public partial class NewProjectWindow : Window
{
    public ProjectInfo? CreatedProject { get; private set; }

    public NewProjectWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();

        MediaTypeComboBox.ItemsSource = settings.MediaTypes;
        if (settings.MediaTypes.Count > 0)
        {
            MediaTypeComboBox.SelectedIndex = 0;
        }

        Loaded += (_, _) => ProjectNameTextBox.Focus();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "登録するフォルダーを選択",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderPathTextBox.Text = dialog.FolderName;
            ValidationMessage.Text = string.Empty;
        }
    }

    private void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;

        var projectName = ProjectNameTextBox.Text.Trim();
        if (projectName.Length == 0)
        {
            ShowValidationError("プロジェクト名を入力してください。", ProjectNameTextBox);
            return;
        }

        if (MediaTypeComboBox.SelectedItem is not MediaTypeDefinition mediaType)
        {
            ShowValidationError("メディア種別を選択してください。", MediaTypeComboBox);
            return;
        }

        var folderPath = FolderPathTextBox.Text.Trim();
        if (folderPath.Length == 0 || !Directory.Exists(folderPath))
        {
            ShowValidationError("登録するフォルダーを選択してください。", BrowseFolderButton);
            return;
        }

        CreatedProject = new ProjectInfo
        {
            Name = projectName,
            MediaType = mediaType.Name,
            Folders = [Path.GetFullPath(folderPath)]
        };

        DialogResult = true;
    }

    private void ShowValidationError(string message, IInputElement inputElement)
    {
        ValidationMessage.Text = message;
        if (inputElement is UIElement element)
        {
            element.Focus();
        }
    }
}
