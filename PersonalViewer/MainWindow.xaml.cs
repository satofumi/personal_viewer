using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Win32;
using System.ComponentModel;
using PersonalViewer.Configuration;
using PersonalViewer.Localization;
using PersonalViewer.Projects;
using PersonalViewer.Thumbnails;

namespace PersonalViewer;

public partial class MainWindow : Window
{
    private static string ProjectSelectorPlaceholder => LocalizationService.GetString("SelectProject");
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
    private ThumbnailSortOrder _thumbnailSortOrder = ThumbnailSortOrder.FileName;
    private bool _suppressThumbnailSortSave = true;
    private bool _showThumbnailView;
    private bool _isTagInputVisible;
    private CancellationTokenSource? _thumbnailLoadCancellation;
    private CancellationTokenSource? _keywordSearchCancellation;
    private SearchResultItem? _contextMenuTarget;
    private int _ffmpegThumbnailErrorNotificationShown;

    public ProjectInfo? CurrentProject { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        UpdateSortHeaders();
        var app = (App)Application.Current;
        _thumbnailSortOrder = string.Equals(app.Settings.ThumbnailSortOrder, "last_modified", StringComparison.OrdinalIgnoreCase)
            ? ThumbnailSortOrder.LastModified
            : ThumbnailSortOrder.FileName;
        _suppressThumbnailSortSave = true;
        ThumbnailSortComboBox.SelectedIndex = _thumbnailSortOrder == ThumbnailSortOrder.LastModified ? 1 : 0;
        _suppressThumbnailSortSave = false;
        RestoreWindowBounds(app.Settings);
        SetResultsView(details: !string.Equals(app.Settings.LastViewMode, "thumbnails", StringComparison.Ordinal));
        _statusMessageTimer.Tick += StatusMessageTimer_Tick;
        Closed += (_, _) =>
        {
            _statusMessageTimer.Stop();
            CancelThumbnailLoading();
            CancelKeywordSearch();
        };
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
        ShowStatusMessage(LocalizationService.GetString("StatusReady"));
        LoadProjectsAndRestoreSelection();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
    }

    private void RestoreWindowBounds(AppSettings settings)
    {
        if (settings.WindowLeft is not double left
            || settings.WindowTop is not double top
            || settings.WindowWidth is not double width
            || settings.WindowHeight is not double height
            || width < MinWidth
            || height < MinHeight)
        {
            return;
        }

        var savedBounds = new Rect(left, top, width, height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var visibleBounds = savedBounds;
        visibleBounds.Intersect(virtualScreen);
        if (visibleBounds.IsEmpty
            || visibleBounds.Width < Math.Min(savedBounds.Width, 160)
            || visibleBounds.Height < Math.Min(savedBounds.Height, 100))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = savedBounds.Left;
        Top = savedBounds.Top;
        Width = savedBounds.Width;
        Height = savedBounds.Height;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        try
        {
            SettingsStore.SaveWindowBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("WindowBoundsSaveErrorDetails", exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
                LocalizationService.Format("ProjectListLoadErrorDetails", exception.Message),
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
            ShowStatusMessage(LocalizationService.Format("LastProjectOpened", lastProject.Name));
        }
        else if (lastProjectId is not null)
        {
            SaveLastProjectId(null);
            ShowStatusMessage(LocalizationService.GetString("LastProjectMissing"));
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
            ShowStatusMessage(LocalizationService.Format("ProjectSelected", CurrentProject.Name));
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

        var rootItems = CurrentProject.Folders
            .Select(folder => CreateFolderTreeItem(folder, folder))
            .ToArray();
        foreach (var rootItem in rootItems)
        {
            FolderTreeView.Items.Add(rootItem);
        }

        if (rootItems.Length == 1 && rootItems[0].HasItems)
        {
            rootItems[0].IsExpanded = true;
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
            Header = LocalizationService.GetString("Rescan"),
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
                SetSearchResults([], LocalizationService.GetString("EmptyResultsPrompt"));
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
            SetSearchResults([], LocalizationService.GetString("SelectProjectMessage"));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _keywordSearchCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        SetSearchResults([], LocalizationService.GetString("Searching"));

        try
        {
            await Task.Delay(KeywordSearchDebounceDuration);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var results = await Task.Run(() => SearchProjectFiles(project, query, cancellationToken));

            if (IsCurrentKeywordSearch(query, cancellation))
            {
                SetSearchResults(results, LocalizationService.GetString("NoMatchingFiles"));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException)
        {
            if (IsCurrentKeywordSearch(query, cancellation))
            {
                SetSearchResults([], LocalizationService.GetString("FileListLoadErrorStatus"));
                MessageBox.Show(
                    this,
                    LocalizationService.Format("FileListLoadErrorDetails", exception.Message),
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
        if (cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        var indexedFiles = _projectFileIndexStore.Load(project);
        var results = new List<SearchResultItem>();
        foreach (var file in indexedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return [];
            }

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
            SetSearchResults([], LocalizationService.GetString("SelectProjectMessage"));
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
            SetSearchResults(results, LocalizationService.Format("FolderNoFiles", Path.GetFileName(folderPath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException)
        {
            SetSearchResults([], LocalizationService.GetString("FileListLoadErrorStatus"));
            MessageBox.Show(
                this,
                LocalizationService.Format("FileListLoadErrorDetails", exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetSearchResults(IReadOnlyCollection<SearchResultItem> results, string emptyMessage)
    {
        var sortedResults = SortResultsForCurrentView(results);
        ResultsListView.SelectedItems.Clear();
        ResultsListView.ItemsSource = sortedResults;
        var resultCountKey = results.Count == 1 ? "ResultCountOne" : "ResultCountMany";
        ResultCountText.Text = LocalizationService.Format(resultCountKey, results.Count);
        ResultEmptyText.Text = emptyMessage;
        ResultEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_showThumbnailView)
        {
            LoadThumbnails(sortedResults);
        }
    }

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetTagInputVisible(visible: false);
        RefreshSelectedTagsPanel();
    }

    private void RefreshSelectedTagsPanel()
    {
        var selectedFiles = ResultsListView.SelectedItems
            .OfType<SearchResultItem>()
            .ToArray();

        AddTagButton.IsEnabled = selectedFiles.Length > 0 && CurrentProject is not null;
        if (selectedFiles.Length == 0)
        {
            SelectedTagsItemsControl.ItemsSource = null;
            SelectedTagsItemsControl.Visibility = Visibility.Collapsed;
            TagPlaceholderText.Text = LocalizationService.GetString("SelectFilesTagsPlaceholder");
            TagPlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        var tagItems = selectedFiles.Length == 1
            ? selectedFiles[0].File.Tags
                .Select(tag => new TagDisplayItem(tag, string.Empty, CanRemove: true))
                .ToArray()
            : AggregateSelectedTags(selectedFiles);

        SelectedTagsItemsControl.ItemsSource = tagItems;
        SelectedTagsItemsControl.Visibility = tagItems.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TagPlaceholderText.Text = selectedFiles.Length == 1
            ? LocalizationService.GetString("NoTags")
            : LocalizationService.GetString("NoTagsForSelectedFiles");
        TagPlaceholderText.Visibility = tagItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TagDisplayItem[] AggregateSelectedTags(IReadOnlyCollection<SearchResultItem> selectedFiles)
    {
        var tagCounts = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selectedFiles)
        {
            foreach (var tag in file.File.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (tagCounts.TryGetValue(tag, out var entry))
                {
                    tagCounts[tag] = (entry.Name, entry.Count + 1);
                }
                else
                {
                    tagCounts[tag] = (tag, 1);
                }
            }
        }

        return tagCounts.Values
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new TagDisplayItem(
                entry.Name,
                $"{entry.Count}/{selectedFiles.Count}",
                CanRemove: true))
            .ToArray();
    }

    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsListView.SelectedItems.Count == 0 || CurrentProject is null)
        {
            return;
        }

        if (_isTagInputVisible)
        {
            AddTagFromInput();
        }
        else
        {
            SetTagInputVisible(visible: true);
        }
    }

    private void NewTagTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddTagFromInput();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetTagInputVisible(visible: false);
        }
    }

    private void AddTagFromInput()
    {
        var selectedFiles = GetSelectedResultFiles();
        if (selectedFiles.Length == 0 || CurrentProject is null)
        {
            return;
        }

        var tag = NewTagTextBox.Text.Trim();
        if (tag.Length == 0)
        {
            return;
        }

        var updates = selectedFiles
            .Where(file => !file.File.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(file => (Item: file, Tags: (IEnumerable<string>)file.File.Tags.Append(tag)))
            .ToArray();
        if (updates.Length == 0)
        {
            ShowStatusMessage(LocalizationService.GetString("TagAlreadyAdded"));
            return;
        }

        if (SaveTagsForFiles(updates, LocalizationService.GetString("TagAdded")))
        {
            SetTagInputVisible(visible: false);
        }
    }

    private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetSelectedResultFiles();
        if (selectedFiles.Length == 0
            || sender is not Button { Tag: string tag })
        {
            return;
        }

        var updates = selectedFiles
            .Where(file => file.File.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(file => (
                Item: file,
                Tags: (IEnumerable<string>)file.File.Tags
                    .Where(existingTag => !StringComparer.OrdinalIgnoreCase.Equals(existingTag, tag))))
            .ToArray();
        if (updates.Length == 0)
        {
            return;
        }

        SaveTagsForFiles(updates, LocalizationService.GetString("TagRemoved"));
    }

    private SearchResultItem[] GetSelectedResultFiles()
    {
        return ResultsListView.SelectedItems
            .OfType<SearchResultItem>()
            .ToArray();
    }

    private bool SaveTagsForFiles(
        IReadOnlyCollection<(SearchResultItem Item, IEnumerable<string> Tags)> updates,
        string successMessage)
    {
        if (updates.Count == 0 || CurrentProject is null)
        {
            return false;
        }

        var normalizedUpdates = updates.ToDictionary(
            update => update.Item.File.Path,
            update => (IEnumerable<string>)update.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            _projectFileIndexStore.SaveTags(CurrentProject, normalizedUpdates);
            foreach (var update in updates)
            {
                update.Item.File.Tags = normalizedUpdates[update.Item.File.Path].ToList();
            }

            RefreshSelectedTagsPanel();
            ShowStatusMessage(successMessage);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("TagSaveErrorDetails", exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void SetTagInputVisible(bool visible)
    {
        _isTagInputVisible = visible;
        NewTagTextBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddTagButton.Content = visible ? "✓" : "+";
        AddTagButton.FontSize = visible ? 16 : 19;
        AddTagButton.ToolTip = visible ? LocalizationService.GetString("AddEnteredTag") : LocalizationService.GetString("AddTag");

        if (visible)
        {
            NewTagTextBox.Clear();
            NewTagTextBox.Focus();
            Keyboard.Focus(NewTagTextBox);
        }
        else
        {
            NewTagTextBox.Clear();
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
                LocalizationService.Format("ViewModeSaveErrorDetails", exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void SaveThumbnailSortOrder(ThumbnailSortOrder sortOrder)
    {
        var settingValue = sortOrder == ThumbnailSortOrder.LastModified ? "last_modified" : "name";
        var app = (App)Application.Current;
        app.Settings.ThumbnailSortOrder = settingValue;
        try
        {
            SettingsStore.SaveThumbnailSortOrder(settingValue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsFileException)
        {
            MessageBox.Show(
                LocalizationService.Format("ThumbnailSortSaveErrorDetails", exception.Message),
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
        ThumbnailSortPanel.Visibility = details ? Visibility.Collapsed : Visibility.Visible;

        if (ResultsListView.ItemsSource is IReadOnlyCollection<SearchResultItem> currentResults)
        {
            var selectedPaths = ResultsListView.SelectedItems
                .OfType<SearchResultItem>()
                .Select(result => result.File.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sortedResults = details
                ? SortSearchResults(currentResults)
                : SortThumbnailResults(currentResults);
            ResultsListView.ItemsSource = sortedResults;
            foreach (var result in sortedResults.Where(result => selectedPaths.Contains(result.File.Path)))
            {
                ResultsListView.SelectedItems.Add(result);
            }

            if (details)
            {
                CancelThumbnailLoading();
            }
            else
            {
                LoadThumbnails(sortedResults);
            }
        }
        else if (details)
        {
            CancelThumbnailLoading();
        }
    }

    private void ThumbnailSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailSortComboBox.SelectedIndex is < 0 or > 1)
        {
            return;
        }

        _thumbnailSortOrder = ThumbnailSortComboBox.SelectedIndex == 0
            ? ThumbnailSortOrder.FileName
            : ThumbnailSortOrder.LastModified;
        if (!_suppressThumbnailSortSave)
        {
            SaveThumbnailSortOrder(_thumbnailSortOrder);
        }

        if (!_showThumbnailView
            || ResultsListView.ItemsSource is not IReadOnlyCollection<SearchResultItem> results)
        {
            return;
        }

        var selectedPaths = ResultsListView.SelectedItems
            .OfType<SearchResultItem>()
            .Select(result => result.File.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sortedResults = SortThumbnailResults(results);
        ResultsListView.ItemsSource = sortedResults;
        foreach (var result in sortedResults.Where(result => selectedPaths.Contains(result.File.Path)))
        {
            ResultsListView.SelectedItems.Add(result);
        }
    }

    private void LoadThumbnails(IEnumerable<SearchResultItem> results)
    {
        CancelThumbnailLoading();
        var cancellationSource = new CancellationTokenSource();
        _thumbnailLoadCancellation = cancellationSource;
        Interlocked.Exchange(ref _ffmpegThumbnailErrorNotificationShown, 0);
        var allowFfmpegFallback = StringComparer.OrdinalIgnoreCase.Equals(CurrentProject?.MediaType, "Video");
        foreach (var result in results)
        {
            _ = LoadThumbnailAsync(result, allowFfmpegFallback, cancellationSource.Token);
        }
    }

    private async Task LoadThumbnailAsync(SearchResultItem result, bool allowFfmpegFallback, CancellationToken cancellationToken)
    {
        try
        {
            var thumbnailData = await _thumbnailCache.GetThumbnailAsync(result.File, allowFfmpegFallback, cancellationToken);
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
        catch (FfmpegThumbnailException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _ffmpegThumbnailErrorNotificationShown, 1) == 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        ShowStatusMessage(LocalizationService.Format(
                            "FfmpegThumbnailFailure",
                            Path.GetFileName(exception.FilePath)));
                    }
                }, DispatcherPriority.Background);
            }
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
        var labels = new[]
        {
            LocalizationService.GetString("ColumnFileName"),
            LocalizationService.GetString("ColumnLastModified"),
            LocalizationService.GetString("ColumnType"),
            LocalizationService.GetString("ColumnSize")
        };
        for (var index = 0; index < labels.Length; index++)
        {
            var indicator = index == (int)_sortColumn
                ? _sortAscending ? " ▲" : " ▼"
                : string.Empty;
            DetailsGridView.Columns[index].Header = labels[index] + indicator;
        }
    }

    private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsListView, source) is not ListViewItem item
            || item.DataContext is not SearchResultItem result)
        {
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.File.Path,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("FileOpenErrorDetails", result.File.Path, exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ResultsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextMenuTarget = null;
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsListView, source) is not ListViewItem item
            || item.DataContext is not SearchResultItem result)
        {
            return;
        }

        _contextMenuTarget = result;
        if (!item.IsSelected)
        {
            ResultsListView.SelectedItems.Clear();
            item.IsSelected = true;
        }

        item.Focus();
    }

    private void ResultsListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_contextMenuTarget is null)
        {
            e.Handled = true;
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var result = _contextMenuTarget;
        _contextMenuTarget = null;
        if (result is null)
        {
            return;
        }

        if (!File.Exists(result.File.Path))
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("FileNotFoundRescanDetails", result.File.Path),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.File.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("ExplorerOpenErrorDetails", result.File.Path, exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private IReadOnlyCollection<SearchResultItem> SortResultsForCurrentView(IEnumerable<SearchResultItem> results)
    {
        return _showThumbnailView
            ? SortThumbnailResults(results)
            : SortSearchResults(results);
    }

    private IReadOnlyCollection<SearchResultItem> SortThumbnailResults(IEnumerable<SearchResultItem> results)
    {
        IOrderedEnumerable<SearchResultItem> sorted = _thumbnailSortOrder switch
        {
            ThumbnailSortOrder.FileName => results.OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase),
            ThumbnailSortOrder.LastModified => results.OrderBy(result => result.File.LastModifiedUtc),
            _ => results.OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
        };

        return sorted
            .ThenBy(result => result.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
                Header = LocalizationService.GetString("FolderUnreadable"),
                IsEnabled = false
            });
            ShowStatusMessage(LocalizationService.Format("SubfolderLoadError", Path.GetFileName(folderNode.FullPath)));
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
                LocalizationService.Format("SaveLastProjectErrorDetails", exception.Message),
                "Personal Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? LocalizationService.GetString("VersionUnknown");
        MessageBox.Show(
            this,
            LocalizationService.Format("AboutMessage", version),
            LocalizationService.GetString("AboutTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
                LocalizationService.Format("ProjectCreateErrorDetails", exception.Message),
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
            LocalizationService.GetString("ProjectScanProgress"),
            LocalizationService.GetString("ProjectScanComplete"),
            LocalizationService.GetString("ProjectScanFailure"));
    }

    private async void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanInProgress)
        {
            return;
        }

        if (CurrentProject is null)
        {
            ShowStatusMessage(LocalizationService.GetString("SelectProjectFirst"));
            return;
        }

        var project = CurrentProject;
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.GetString("AddFolderDialogTitle"),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folderPath = NormalizeFolderPath(dialog.FolderName);
        if (!Directory.Exists(folderPath))
        {
            ShowStatusMessage(LocalizationService.GetString("FolderMissing"));
            return;
        }

        if (project.Folders.Any(folder =>
                StringComparer.OrdinalIgnoreCase.Equals(NormalizeFolderPath(folder), folderPath)))
        {
            ShowStatusMessage(LocalizationService.GetString("FolderAlreadyRegistered"));
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
                LocalizationService.Format("AddFolderErrorDetails", exception.Message),
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
            LocalizationService.GetString("AddFolderScanProgress"),
            LocalizationService.GetString("AddFolderScanComplete"),
            LocalizationService.GetString("AddFolderScanFailure"));
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
            LocalizationService.Format("RescanProgress", folderName),
            LocalizationService.Format("RescanComplete", folderName),
            LocalizationService.Format("RescanFailure", folderName));
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
                    : LocalizationService.Format("ScanPartialSuccess", completionMessage, issueCount);
                ShowStatusMessage(message);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ProjectFileIndexException or UnknownFileStoreException)
        {
            if (IsVisible)
            {
                MessageBox.Show(
                    this,
                    LocalizationService.Format("ScanFailureDetails", failureMessage, exception.Message),
                    "Personal Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ShowStatusMessage(LocalizationService.GetString("ScanFailureStatus"));
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

    private enum ThumbnailSortOrder
    {
        FileName,
        LastModified
    }

    private sealed record FolderTreeNode(string FullPath, string RootPath, bool HasSubfolders);

    private sealed record TagDisplayItem(string Name, string CountText, bool CanRemove);

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
