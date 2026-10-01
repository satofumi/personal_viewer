using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.ComponentModel;
using PersonalViewer.Configuration;
using PersonalViewer.Projects;
using PersonalViewer.Thumbnails;

namespace PersonalViewer;

public partial class MainWindow : Window
{
    private const string ProjectSelectorPlaceholder = "プロジェクトを選択";
    private static readonly object LazyChildPlaceholder = new();
    private static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeywordSearchDebounceDuration = TimeSpan.FromMilliseconds(180);

    private readonly ProjectStore _projectStore = new();
    private readonly DispatcherTimer _statusMessageTimer = new()
    {
        Interval = StatusMessageDuration
    };
    private readonly MediaFileScanner _mediaFileScanner = new();
    private readonly ProjectFileIndexStore _projectFileIndexStore = new();
    private readonly ProjectFileIndexUpdater _projectFileIndexUpdater = new(new ProjectFileIndexStore(), new UnknownFileStore());
    private readonly WindowsThumbnailCache _thumbnailCache = new();
    private IReadOnlyList<ProjectInfo> _projects = [];
    private bool _suppressProjectSelectionChanged;
    private bool _settingFolderSearchText;
    private bool _scanInProgress;
    private SearchMode _searchMode;
    private ResultSortColumn _sortColumn = ResultSortColumn.FileName;
    private bool _sortAscending = true;
    private bool _showThumbnailView;
    private CancellationTokenSource? _thumbnailLoadCancellation;
    private CancellationTokenSource? _keywordSearchCancellation;

    public ProjectInfo? CurrentProject { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        UpdateSortHeaders();
        var app = (App)Application.Current;
        SetResultsView(details: !string.Equals(app.Settings.LastViewMode, "thumbnails", StringComparison.Ordinal));
        _statusMessageTimer.Tick += StatusMessageTimer_Tick;
        Closed += (_, _) =>
        {
            _statusMessageTimer.Stop();
            CancelThumbnailLoading();
            CancelKeywordSearch();
        };
        Loaded += MainWindow_Loaded;
        ShowStatusMessage("準備完了");
        LoadProjectsAndRestoreSelection();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
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
            ProjectComboBox.Items.Clear();
            var placeholderItem = new ComboBoxItem
            {
                Content = ProjectSelectorPlaceholder
            };
            ProjectComboBox.Items.Add(placeholderItem);

            ComboBoxItem? selectedItem = null;
            foreach (var project in _projects)
            {
                var projectItem = new ComboBoxItem
                {
                    Content = project.Name,
                    Tag = project
                };
                ProjectComboBox.Items.Add(projectItem);
                if (selectedProject is not null
                    && StringComparer.OrdinalIgnoreCase.Equals(project.ProjectId, selectedProject.ProjectId))
                {
                    selectedItem = projectItem;
                }
            }

            ProjectComboBox.SelectedItem = selectedItem ?? placeholderItem;
            CurrentProject = selectedItem?.Tag as ProjectInfo;
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

        CurrentProject = (ProjectComboBox.SelectedItem as ComboBoxItem)?.Tag as ProjectInfo;
        RefreshFolderTree();
        SaveLastProjectId(CurrentProject?.ProjectId);
        if (CurrentProject is not null)
        {
            ShowStatusMessage($"プロジェクト「{CurrentProject.Name}」を選択しました。");
        }
    }

    private void RefreshFolderTree()
    {
        SearchTextBox.Clear();
        FolderTreeView.Items.Clear();
        FolderPlaceholderText.Visibility = CurrentProject is null ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentProject is null)
        {
            return;
        }

        foreach (var folder in CurrentProject.Folders)
        {
            FolderTreeView.Items.Add(CreateFolderTreeItem(folder, folder));
        }
    }

    private TreeViewItem CreateFolderTreeItem(string folderPath, string rootPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var folderName = new DirectoryInfo(fullPath).Name;
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = fullPath;
        }

        var folderNode = new FolderTreeNode(fullPath, Path.GetFullPath(rootPath), HasSubfolders(fullPath));
        var item = new TreeViewItem
        {
            Header = CreateFolderHeader(folderName),
            Tag = folderNode
        };
        var rescanMenuItem = new MenuItem
        {
            Header = "再スキャン",
            Tag = folderNode
        };
        rescanMenuItem.Click += RescanFolderMenuItem_Click;
        item.ContextMenu = new ContextMenu();
        item.ContextMenu.Items.Add(rescanMenuItem);
        item.PreviewMouseRightButtonDown += FolderTreeItem_PreviewMouseRightButtonDown;
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

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: FolderTreeNode folderNode })
        {
            return;
        }

        _settingFolderSearchText = true;
        try
        {
            SearchTextBox.Text = GetFolderSearchText(folderNode);
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }
        finally
        {
            _settingFolderSearchText = false;
        }
    }

    private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (string.IsNullOrEmpty(SearchTextBox.Text))
        {
            _searchMode = SearchMode.Empty;
        }
        else if (_settingFolderSearchText
                 && FolderTreeView.SelectedItem is TreeViewItem { Tag: FolderTreeNode })
        {
            _searchMode = SearchMode.Folder;
        }
        else
        {
            _searchMode = SearchMode.Keyword;
        }

        switch (_searchMode)
        {
            case SearchMode.Empty:
                CancelKeywordSearch();
                SetSearchResults([], "検索欄にキーワードを入力するか、フォルダーを選択してください");
                break;
            case SearchMode.Folder when FolderTreeView.SelectedItem is TreeViewItem { Tag: FolderTreeNode folderNode }:
                CancelKeywordSearch();
                ShowFolderResults(folderNode);
                break;
            case SearchMode.Keyword:
                await ShowKeywordSearchResultsAsync(SearchTextBox.Text, CurrentProject);
                break;
        }
    }

    private async Task ShowKeywordSearchResultsAsync(string query, ProjectInfo? project)
    {
        CancelKeywordSearch();
        if (project is null)
        {
            SetSearchResults([], "プロジェクトを選択してください");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _keywordSearchCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        SetSearchResults([], "検索しています...");

        try
        {
            await Task.Delay(KeywordSearchDebounceDuration, cancellationToken);
            var results = await Task.Run(
                () => SearchProjectFiles(project, query, cancellationToken),
                cancellationToken);

            if (IsCurrentKeywordSearch(query, cancellation))
            {
                SetSearchResults(results, "一致するファイルはありません");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException)
        {
            if (IsCurrentKeywordSearch(query, cancellation))
            {
                SetSearchResults([], "ファイル一覧を読み込めませんでした");
                MessageBox.Show(
                    this,
                    $"ファイル一覧を読み込めませんでした。{Environment.NewLine}{exception.Message}",
                    "Personal Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_keywordSearchCancellation, cancellation))
            {
                _keywordSearchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private IReadOnlyCollection<SearchResultItem> SearchProjectFiles(
        ProjectInfo project,
        string query,
        CancellationToken cancellationToken)
    {
        var indexedFiles = _projectFileIndexStore.Load(project);
        var results = new List<SearchResultItem>();
        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file.Path);
            if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || file.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new SearchResultItem(file, fileName));
            }
        }

        return results
            .OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool IsCurrentKeywordSearch(string query, CancellationTokenSource cancellation)
    {
        return !cancellation.IsCancellationRequested
            && IsVisible
            && _searchMode == SearchMode.Keyword
            && StringComparer.Ordinal.Equals(SearchTextBox.Text, query);
    }

    private void CancelKeywordSearch()
    {
        var cancellation = _keywordSearchCancellation;
        _keywordSearchCancellation = null;
        cancellation?.Cancel();
    }

    private void ShowFolderResults(FolderTreeNode folderNode)
    {
        if (CurrentProject is null)
        {
            SetSearchResults([], "プロジェクトを選択してください");
            return;
        }

        try
        {
            var folderPath = Path.GetFullPath(folderNode.FullPath);
            var results = _projectFileIndexStore.Load(CurrentProject)
                .Where(file => StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(file.Path), folderPath))
                .OrderBy(file => Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase)
                .Select(file => new SearchResultItem(file, Path.GetFileName(file.Path)))
                .ToArray();
            SetSearchResults(results, $"フォルダー「{Path.GetFileName(folderPath)}」に表示できるファイルはありません");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException)
        {
            SetSearchResults([], "ファイル一覧を読み込めませんでした");
            MessageBox.Show(
                this,
                $"ファイル一覧を読み込めませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetSearchResults(IReadOnlyCollection<SearchResultItem> results, string emptyMessage)
    {
        var sortedResults = SortSearchResults(results);
        ResultsListView.ItemsSource = sortedResults;
        ResultCountText.Text = $"{results.Count} 件";
        ResultEmptyText.Text = emptyMessage;
        ResultEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_showThumbnailView)
        {
            LoadThumbnails(sortedResults);
        }
    }

    private void DetailsViewButton_Click(object sender, RoutedEventArgs e)
    {
        SetResultsView(details: true);
        SaveLastViewMode("details");
    }

    private void ThumbnailViewButton_Click(object sender, RoutedEventArgs e)
    {
        SetResultsView(details: false);
        SaveLastViewMode("thumbnails");
    }

    private static void SaveLastViewMode(string viewMode)
    {
        var app = (App)Application.Current;
        app.Settings.LastViewMode = viewMode;
        try
        {
            SettingsStore.SaveLastViewMode(viewMode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            MessageBox.Show(
                $"表示モードを保存できませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetResultsView(bool details)
    {
        _showThumbnailView = !details;
        DetailsViewButton.IsChecked = details;
        ThumbnailViewButton.IsChecked = !details;
        ResultsListView.View = details ? DetailsGridView : null;
        ResultsListView.ItemTemplate = details
            ? null
            : (DataTemplate)FindResource("ThumbnailResultTemplate");
        ResultsListView.ItemsPanel = details
            ? (ItemsPanelTemplate)FindResource("ResultsItemsPanelTemplate")
            : (ItemsPanelTemplate)FindResource("ThumbnailItemsPanelTemplate");

        if (details)
        {
            CancelThumbnailLoading();
        }
        else if (ResultsListView.ItemsSource is IReadOnlyCollection<SearchResultItem> results)
        {
            LoadThumbnails(results);
        }
    }

    private void LoadThumbnails(IEnumerable<SearchResultItem> results)
    {
        CancelThumbnailLoading();
        var cancellationSource = new CancellationTokenSource();
        _thumbnailLoadCancellation = cancellationSource;
        foreach (var result in results)
        {
            _ = LoadThumbnailAsync(result, cancellationSource.Token);
        }
    }

    private async Task LoadThumbnailAsync(SearchResultItem result, CancellationToken cancellationToken)
    {
        try
        {
            var thumbnailData = await _thumbnailCache.GetThumbnailAsync(result.File, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    result.Thumbnail = CreateThumbnail(thumbnailData);
                }
            }, DispatcherPriority.DataBind);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A single file without an available thumbnail should not prevent other results from appearing.
        }
    }

    private static ImageSource? CreateThumbnail(byte[]? thumbnailData)
    {
        if (thumbnailData is null)
        {
            return null;
        }

        using var stream = new MemoryStream(thumbnailData, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void CancelThumbnailLoading()
    {
        _thumbnailLoadCancellation?.Cancel();
        _thumbnailLoadCancellation?.Dispose();
        _thumbnailLoadCancellation = null;
    }

    private void ResultsListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Role == GridViewColumnHeaderRole.Padding
            || header.Column is null)
        {
            return;
        }

        var columnIndex = DetailsGridView.Columns.IndexOf(header.Column);
        if (columnIndex < 0 || columnIndex > (int)ResultSortColumn.Size)
        {
            return;
        }

        var clickedColumn = (ResultSortColumn)columnIndex;
        if (_sortColumn == clickedColumn)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = clickedColumn;
            _sortAscending = true;
        }

        UpdateSortHeaders();
        if (ResultsListView.ItemsSource is IReadOnlyCollection<SearchResultItem> results)
        {
            ResultsListView.ItemsSource = SortSearchResults(results);
        }
    }

    private void UpdateSortHeaders()
    {
        var labels = new[] { "ファイル名", "更新日時", "種類", "サイズ" };
        for (var index = 0; index < labels.Length; index++)
        {
            var indicator = index == (int)_sortColumn
                ? _sortAscending ? " ▲" : " ▼"
                : string.Empty;
            DetailsGridView.Columns[index].Header = labels[index] + indicator;
        }
    }

    private IReadOnlyCollection<SearchResultItem> SortSearchResults(IEnumerable<SearchResultItem> results)
    {
        IOrderedEnumerable<SearchResultItem> sorted = _sortColumn switch
        {
            ResultSortColumn.FileName => OrderBy(results, result => result.FileName, StringComparer.OrdinalIgnoreCase),
            ResultSortColumn.LastModified => OrderBy(results, result => result.File.LastModifiedUtc),
            ResultSortColumn.FileType => OrderBy(results, result => result.FileType, StringComparer.OrdinalIgnoreCase),
            ResultSortColumn.Size => OrderBy(results, result => result.File.SizeBytes),
            _ => OrderBy(results, result => result.FileName, StringComparer.OrdinalIgnoreCase)
        };

        return sorted
            .ThenBy(result => result.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IOrderedEnumerable<SearchResultItem> OrderBy<TKey>(
        IEnumerable<SearchResultItem> results,
        Func<SearchResultItem, TKey> selector,
        IComparer<TKey>? comparer = null)
    {
        return _sortAscending
            ? results.OrderBy(selector, comparer)
            : results.OrderByDescending(selector, comparer);
    }

    private static string GetFolderSearchText(FolderTreeNode folderNode)
    {
        var rootName = new DirectoryInfo(folderNode.RootPath).Name;
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = folderNode.RootPath;
        }

        var relativePath = Path.GetRelativePath(folderNode.RootPath, folderNode.FullPath);
        var projectRelativePath = relativePath == "."
            ? rootName
            : Path.Combine(rootName, relativePath);
        return $":folder:{projectRelativePath}";
    }

    private void FolderTreeItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
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
                item.Items.Add(CreateFolderTreeItem(subfolder, folderNode.RootPath));
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

    private async void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanInProgress)
        {
            return;
        }

        var app = (App)Application.Current;
        var dialog = new NewProjectWindow(app.Settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.CreatedProject is null)
        {
            return;
        }

        ProjectInfo savedProject;
        try
        {
            savedProject = _projectStore.Save(dialog.CreatedProject);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectDataException or SecurityException)
        {
            MessageBox.Show(
                $"プロジェクトを作成できませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        UpsertProject(savedProject);
        SetProjectItems(savedProject);
        SaveLastProjectId(savedProject.ProjectId);
        await ScanAndSaveIndexAsync(
            savedProject,
            app.Settings,
            folderPath: null,
            "プロジェクトのフォルダーをスキャンしています...",
            "プロジェクトを作成し、スキャンしました。",
            "プロジェクトは作成しましたが、初回スキャンに失敗しました。");
    }

    private async void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanInProgress)
        {
            return;
        }

        if (CurrentProject is null)
        {
            ShowStatusMessage("先にプロジェクトを選択してください。");
            return;
        }

        var project = CurrentProject;
        var dialog = new OpenFolderDialog
        {
            Title = "追加するフォルダーを選択",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folderPath = NormalizeFolderPath(dialog.FolderName);
        if (!Directory.Exists(folderPath))
        {
            ShowStatusMessage("選択したフォルダーが見つかりません。");
            return;
        }

        if (project.Folders.Any(folder =>
                StringComparer.OrdinalIgnoreCase.Equals(NormalizeFolderPath(folder), folderPath)))
        {
            ShowStatusMessage("そのフォルダーは既にプロジェクトに登録されています。");
            return;
        }

        var updatedProject = new ProjectInfo
        {
            ProjectId = project.ProjectId,
            Name = project.Name,
            MediaType = project.MediaType,
            Folders = project.Folders.Append(folderPath).ToList()
        };

        ProjectInfo savedProject;
        try
        {
            savedProject = _projectStore.Save(updatedProject);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectDataException or SecurityException)
        {
            MessageBox.Show(
                $"フォルダーをプロジェクトに追加できませんでした。{Environment.NewLine}{exception.Message}",
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        UpsertProject(savedProject);
        var selectedProject = CurrentProject is not null
            && StringComparer.OrdinalIgnoreCase.Equals(CurrentProject.ProjectId, savedProject.ProjectId)
                ? savedProject
                : CurrentProject;
        SetProjectItems(selectedProject);
        await ScanAndSaveIndexAsync(
            savedProject,
            ((App)Application.Current).Settings,
            folderPath,
            "追加したフォルダーをスキャンしています...",
            "フォルダーを追加し、スキャンしました。",
            "フォルダーは追加しましたが、そのフォルダーのスキャンに失敗しました。");
    }

    private async void RescanFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanInProgress || sender is not MenuItem { Tag: FolderTreeNode folderNode })
        {
            return;
        }

        var project = CurrentProject;
        if (project is null)
        {
            return;
        }

        var app = (App)Application.Current;
        var folderName = Path.GetFileName(folderNode.FullPath);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = folderNode.FullPath;
        }

        await ScanAndSaveIndexAsync(
            project,
            app.Settings,
            folderNode.FullPath,
            $"フォルダー「{folderName}」を再スキャンしています...",
            $"フォルダー「{folderName}」を再スキャンしました。",
            $"フォルダー「{folderName}」の再スキャンに失敗しました。");
    }

    private void UpsertProject(ProjectInfo project)
    {
        _projects = _projects
            .Where(existing => !StringComparer.OrdinalIgnoreCase.Equals(existing.ProjectId, project.ProjectId))
            .Append(project)
            .OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ScanAndSaveIndexAsync(
        ProjectInfo project,
        AppSettings settings,
        string? folderPath,
        string progressMessage,
        string completionMessage,
        string failureMessage)
    {
        SetScanInProgress(true);
        ShowStatusMessage(progressMessage, dismissAfterDelay: false);
        try
        {
            var scanResult = await Task.Run(() =>
                folderPath is null
                    ? _mediaFileScanner.Scan(project, settings)
                    : _mediaFileScanner.ScanFolder(project, folderPath, settings));

            var scannedFolders = folderPath is null ? project.Folders : [folderPath];
            await Task.Run(() => _projectFileIndexUpdater.UpdateAfterScan(
                project,
                scannedFolders,
                scanResult.Files,
                scanResult.IncompletePaths));

            if (IsVisible)
            {
                var issueCount = scanResult.Issues
                    .Select(issue => issue.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var message = issueCount == 0
                    ? completionMessage
                    : $"{completionMessage} 一部を読み込めませんでした（{issueCount} 件）。";
                ShowStatusMessage(message);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException or UnknownFileStoreException)
        {
            if (IsVisible)
            {
                MessageBox.Show(
                    this,
                    $"{failureMessage}{Environment.NewLine}{exception.Message}",
                    "Personal Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ShowStatusMessage("スキャンに失敗しました。");
            }
        }
        finally
        {
            SetScanInProgress(false);
        }
    }

    private void SetScanInProgress(bool inProgress)
    {
        _scanInProgress = inProgress;
        ProjectComboBox.IsEnabled = !inProgress;
        FolderTreeView.IsEnabled = !inProgress;
        NewProjectMenuItem.IsEnabled = !inProgress;
        AddFolderMenuItem.IsEnabled = !inProgress;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
    }

    private enum SearchMode
    {
        Empty,
        Folder,
        Keyword
    }

    private enum ResultSortColumn
    {
        FileName,
        LastModified,
        FileType,
        Size
    }

    private sealed record FolderTreeNode(string FullPath, string RootPath, bool HasSubfolders);

    private sealed record SearchResultItem(IndexedFile File, string FileName) : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        public DateTime LastModifiedLocal => File.LastModifiedUtc.ToLocalTime();

        public string FileType => File.Extension;

        public string FileSize => $"{File.SizeBytes:N0} B";

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                {
                    return;
                }

                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
