using System.IO;
using System.Windows;
using Microsoft.Win32;
using PersonalViewer.Configuration;
using PersonalViewer.Localization;

namespace PersonalViewer.Projects;

public partial class NewProjectWindow : Window
{
    public ProjectInfo? CreatedProject { get; private set; }

    public NewProjectWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();

        MediaTypeComboBox.ItemsSource = settings.MediaTypes
            .Select(mediaType => new MediaTypeOption(
                mediaType,
                GetMediaTypeDisplayName(mediaType.Name)))
            .ToArray();
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
            Title = LocalizationService.GetString("SelectRegisteredFolderDialog"),
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
            ShowValidationError(LocalizationService.GetString("ProjectNameRequired"), ProjectNameTextBox);
            return;
        }

        if (MediaTypeComboBox.SelectedItem is not MediaTypeOption mediaTypeOption)
        {
            ShowValidationError(LocalizationService.GetString("MediaTypeRequired"), MediaTypeComboBox);
            return;
        }

        var mediaType = mediaTypeOption.MediaType;
        var folderPath = FolderPathTextBox.Text.Trim();
        if (folderPath.Length == 0 || !Directory.Exists(folderPath))
        {
            ShowValidationError(LocalizationService.GetString("RegisteredFolderRequired"), BrowseFolderButton);
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

    private static string GetMediaTypeDisplayName(string name)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(name, "Video"))
        {
            return LocalizationService.GetString("MediaTypeVideo");
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(name, "Image"))
        {
            return LocalizationService.GetString("MediaTypeImage");
        }

        return name;
    }

    private sealed record MediaTypeOption(MediaTypeDefinition MediaType, string DisplayName);

    private void ShowValidationError(string message, IInputElement inputElement)
    {
        ValidationMessage.Text = message;
        if (inputElement is UIElement element)
        {
            element.Focus();
        }
    }
}
