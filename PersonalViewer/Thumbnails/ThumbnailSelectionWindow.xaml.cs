using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PersonalViewer.Localization;
using PersonalViewer.Projects;

namespace PersonalViewer.Thumbnails;

public partial class ThumbnailSelectionWindow : Window
{
    private readonly IndexedFile _file;
    private readonly WindowsThumbnailCache _thumbnailCache;
    private readonly CancellationTokenSource _generationCancellation = new();

    public ThumbnailCandidate? SelectedCandidate { get; private set; }

    public Exception? GenerationException { get; private set; }

    public ThumbnailSelectionWindow(IndexedFile file, WindowsThumbnailCache thumbnailCache)
    {
        InitializeComponent();
        _file = file;
        _thumbnailCache = thumbnailCache;
        FileNameTextBlock.Text = Path.GetFileName(file.Path);
        FileNameTextBlock.ToolTip = file.Path;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidates = await _thumbnailCache.GenerateVideoCandidatesAsync(
                _file,
                _generationCancellation.Token);
            if (_generationCancellation.IsCancellationRequested)
            {
                return;
            }

            CandidateListBox.ItemsSource = candidates
                .Select(candidate => new ThumbnailCandidateItem(
                    candidate,
                    CreatePreview(candidate.Data),
                    FormatTime(candidate.Time)))
                .ToArray();
            StatusTextBlock.Text = LocalizationService.GetString("ThumbnailCandidatePrompt");
        }
        catch (OperationCanceledException) when (_generationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            GenerationException = exception;
            StatusTextBlock.Text = LocalizationService.GetString("ThumbnailCandidateFailure");
        }
    }

    private void CandidateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseThumbnailButton.IsEnabled = CandidateListBox.SelectedItem is ThumbnailCandidateItem;
    }

    private void UseThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateListBox.SelectedItem is not ThumbnailCandidateItem selectedItem)
        {
            return;
        }

        SelectedCandidate = selectedItem.Candidate;
        DialogResult = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _generationCancellation.Cancel();
    }

    private static ImageSource CreatePreview(byte[] imageData)
    {
        using var stream = new MemoryStream(imageData, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string FormatTime(TimeSpan time)
    {
        var totalHours = (int)time.TotalHours;
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    public sealed record ThumbnailCandidateItem(
        ThumbnailCandidate Candidate,
        ImageSource Preview,
        string TimeLabel);
}
