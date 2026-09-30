using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PersonalViewer.Configuration;
using PersonalViewer.Projects;

namespace PersonalViewer;

public partial class MainWindow : Window
{
    private static readonly object LazyChildPlaceholder = new();
    private static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(5);

    private readonly ProjectStore _projectStore = new();
    private readonly DispatcherTimer _statusMessageTimer = new()
    {
        Interval = StatusMessageDuration
    };
    private IReadOnlyList<ProjectInfo> _projects = [];
    private bool _suppressProjectSelectionChanged;

    public ProjectInfo? CurrentProject { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        _statusMessageTimer.Tick += StatusMessageTimer_Tick;
        Closed += (_, _) => _statusMessageTimer.Stop();
        ShowStatusMessage("準備完了");
        LoadProjectsAndRestoreSelection();
    }

    private void ShowStatusMessage(string message, bool dismissAfterDelay = true)
    {
        _statusMessageTimer.Stop();
        StatusText.Text = message;
        if (dismissAfterDelay)
        {
            _statusMessageTimer.Start();
        }
    }

    private void StatusMessageTimer_Tick(object? sender, EventArgs e)
    {
        _statusMessageTimer.Stop();
        StatusText.Text = string.Empty;
    }

    private void LoadProjectsAndRestoreSelection()
    {
        try
        {
            _projects = _projectStore.LoadAll();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectDataException)
        {
            _projects = [];
            SetProjectItems(null);
            MessageBox.Show(
                $"プロジェクト一覧を読み込めませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var app = (App)Application.Current;
        var lastProjectId = app.Settings.LastProjectId;
        var lastProject = lastProjectId is null
            ? null
            : _projects.FirstOrDefault(project => StringComparer.OrdinalIgnoreCase.Equals(project.ProjectId, lastProjectId));

        SetProjectItems(lastProject);
        if (lastProject is not null)
        {
            ShowStatusMessage($"前回のプロジェクト「{lastProject.Name}」を開きました。");
        }
        else if (lastProjectId is not null)
        {
            SaveLastProjectId(null);
            ShowStatusMessage("前回選択されたプロジェクトが見つかりません。");
        }
    }

    private void SetProjectItems(ProjectInfo? selectedProject)
    {
        _suppressProjectSelectionChanged = true;
        try
        {
            ProjectComboBox.ItemsSource = _projects;
            ProjectComboBox.SelectedItem = selectedProject;
            CurrentProject = selectedProject;
            RefreshFolderTree();
        }
        finally
        {
            _suppressProjectSelectionChanged = false;
        }
    }

    private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectSelectionChanged)
        {
            return;
        }

        CurrentProject = ProjectComboBox.SelectedItem as ProjectInfo;
        RefreshFolderTree();
        SaveLastProjectId(CurrentProject?.ProjectId);
        if (CurrentProject is not null)
        {
            ShowStatusMessage($"プロジェクト「{CurrentProject.Name}」を選択しました。");
        }
    }

    private void RefreshFolderTree()
    {
        FolderTreeView.Items.Clear();
        FolderPlaceholderText.Visibility = CurrentProject is null ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentProject is null)
        {
            return;
        }

        foreach (var folder in CurrentProject.Folders)
        {
            FolderTreeView.Items.Add(CreateFolderTreeItem(folder));
        }
    }

    private TreeViewItem CreateFolderTreeItem(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var folderName = new DirectoryInfo(fullPath).Name;
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = fullPath;
        }

        var folderNode = new FolderTreeNode(fullPath, HasSubfolders(fullPath));
        var item = new TreeViewItem
        {
            Header = CreateFolderHeader(folderName),
            Tag = folderNode
        };
        if (folderNode.HasSubfolders)
        {
            item.Items.Add(new TreeViewItem
            {
                Header = string.Empty,
                Tag = LazyChildPlaceholder,
                IsEnabled = false
            });
            item.Expanded += FolderTreeItem_Expanded;
        }

        return item;
    }

    private static StackPanel CreateFolderHeader(string folderName)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,1 L 5,1 L 7,3 L 16,3 L 15,12 L 0,12 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(232, 184, 72)),
            Stroke = new SolidColorBrush(Color.FromRgb(177, 126, 35)),
            StrokeThickness = 0.75,
            Width = 16,
            Height = 14,
            Stretch = Stretch.Fill,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        header.Children.Add(new TextBlock
        {
            Text = folderName,
            VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    private static bool HasSubfolders(string folderPath)
    {
        try
        {
            using var directories = Directory.EnumerateDirectories(folderPath).GetEnumerator();
            return directories.MoveNext();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private void FolderTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item
            || item.Items.Count != 1
            || item.Items[0] is not TreeViewItem placeholder
            || !ReferenceEquals(placeholder.Tag, LazyChildPlaceholder)
            || item.Tag is not FolderTreeNode folderNode)
        {
            return;
        }

        item.Items.Clear();
        try
        {
            var subfolders = Directory.EnumerateDirectories(folderNode.FullPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var subfolder in subfolders)
            {
                item.Items.Add(CreateFolderTreeItem(subfolder));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            item.Items.Add(new TreeViewItem
            {
                Header = "（読み込めません）",
                IsEnabled = false
            });
            ShowStatusMessage($"サブフォルダーを読み込めませんでした: {Path.GetFileName(folderNode.FullPath)}");
        }
    }

    private static void SaveLastProjectId(string? projectId)
    {
        var app = (App)Application.Current;
        app.Settings.LastProjectId = projectId;
        try
        {
            SettingsStore.SaveLastProjectId(projectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            MessageBox.Show(
                $"前回選択したプロジェクトを保存できませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var dialog = new NewProjectWindow(app.Settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.CreatedProject is null)
        {
            return;
        }

        try
        {
            var savedProject = _projectStore.Save(dialog.CreatedProject);
            _projects = _projects
                .Where(project => !StringComparer.OrdinalIgnoreCase.Equals(project.ProjectId, savedProject.ProjectId))
                .Append(savedProject)
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetProjectItems(savedProject);
            SaveLastProjectId(savedProject.ProjectId);
            ShowStatusMessage($"プロジェクト「{savedProject.Name}」を作成し、選択しました。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectDataException)
        {
            MessageBox.Show(
                $"プロジェクトを保存できませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private sealed record FolderTreeNode(string FullPath, bool HasSubfolders);
}