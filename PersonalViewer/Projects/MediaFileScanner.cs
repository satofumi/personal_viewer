using System.IO;
using PersonalViewer.Localization;
using System.Security;
using PersonalViewer.Configuration;

namespace PersonalViewer.Projects;

public sealed class MediaFileScanner
{
    public MediaFileScanResult ScanFolder(ProjectInfo project, string folderPath, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(folderPath);

        return Scan(new ProjectInfo
        {
            ProjectId = project.ProjectId,
            Name = project.Name,
            MediaType = project.MediaType,
            Folders = [folderPath]
        }, settings);
    }

    public MediaFileScanResult Scan(ProjectInfo project, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        var mediaType = settings.MediaTypes.FirstOrDefault(candidate =>
            candidate is not null
            && StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, project.MediaType));
        if (mediaType is null)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("MediaTypeNotConfigured", project.MediaType));
        }

        if (mediaType.Extensions is null || mediaType.Extensions.Count == 0)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("MediaExtensionsNotConfigured", mediaType.Name));
        }

        if (project.Folders is null || project.Folders.Count == 0)
        {
            throw new ProjectFileIndexException(LocalizationService.GetString("ProjectFolderMissing"));
        }

        var allowedExtensions = new HashSet<string>(
            mediaType.Extensions
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        var indexedFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<MediaFileScanIssue>();
        var incompletePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? firstEnumerationException = null;
        var successfulEnumerationCount = 0;

        foreach (var folder in project.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Path.IsPathFullyQualified(folder))
            {
                throw new ProjectFileIndexException(LocalizationService.GetString("ScanFolderAbsoluteRequired"));
            }

            var rootPath = System.IO.Path.GetFullPath(folder);
            if (!Directory.Exists(rootPath))
            {
                RecordPartialFailure(rootPath, new DirectoryNotFoundException(LocalizationService.Format("ScanFolderMissing", rootPath)));
                incompletePaths.Add(rootPath);
                continue;
            }

            ScanDirectory(rootPath);
        }

        if (successfulEnumerationCount == 0 && firstEnumerationException is not null)
        {
            throw new IOException(LocalizationService.Format("ScanTargetUnreadable", firstEnumerationException.Message), firstEnumerationException);
        }

        return new MediaFileScanResult(
            indexedFiles.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            issues,
            incompletePaths);

        void ScanDirectory(string directoryPath)
        {
            var childDirectories = new List<string>();
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directoryPath))
                {
                    ScanFile(filePath);
                }

                successfulEnumerationCount++;
            }
            catch (Exception exception) when (IsScanAccessException(exception))
            {
                RecordPartialFailure(directoryPath, exception);
                incompletePaths.Add(directoryPath);
            }

            try
            {
                foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
                {
                    childDirectories.Add(childDirectory);
                }

                successfulEnumerationCount++;
            }
            catch (Exception exception) when (IsScanAccessException(exception))
            {
                RecordPartialFailure(directoryPath, exception);
                incompletePaths.Add(directoryPath);
            }

            foreach (var childDirectory in childDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                ScanDirectory(childDirectory);
            }
        }

        void ScanFile(string filePath)
        {
            var fullPath = System.IO.Path.GetFullPath(filePath);
            var extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension))
            {
                return;
            }

            try
            {
                var fileInfo = new FileInfo(fullPath);
                fileInfo.Refresh();
                if (!fileInfo.Exists)
                {
                    RecordPartialFailure(fullPath, new FileNotFoundException(LocalizationService.GetString("FileDisappeared"), fullPath));
                    return;
                }

                indexedFiles[fullPath] = new IndexedFile
                {
                    Path = fullPath,
                    Extension = extension,
                    SizeBytes = fileInfo.Length,
                    LastModifiedUtc = fileInfo.LastWriteTimeUtc
                };
            }
            catch (Exception exception) when (IsScanAccessException(exception))
            {
                RecordPartialFailure(fullPath, exception);
                if (exception is not FileNotFoundException and not DirectoryNotFoundException)
                {
                    incompletePaths.Add(fullPath);
                }
            }
        }

        void RecordPartialFailure(string path, Exception exception)
        {
            firstEnumerationException ??= exception;
            issues.Add(new MediaFileScanIssue(path, exception.Message));
        }
    }

    private static bool IsScanAccessException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or SecurityException;
    }
}

public sealed class MediaFileScanResult
{
    public MediaFileScanResult(
        IReadOnlyList<IndexedFile> files,
        IReadOnlyList<MediaFileScanIssue> issues,
        IReadOnlyCollection<string> incompletePaths)
    {
        Files = files;
        Issues = issues;
        IncompletePaths = incompletePaths;
    }

    public IReadOnlyList<IndexedFile> Files { get; }

    public IReadOnlyList<MediaFileScanIssue> Issues { get; }

    public IReadOnlyCollection<string> IncompletePaths { get; }
}

public sealed record MediaFileScanIssue(string Path, string Reason);
