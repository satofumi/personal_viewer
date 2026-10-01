using System.IO;

namespace PersonalViewer.Projects;

public sealed class ProjectFileIndexUpdater
{
    private readonly ProjectFileIndexStore _projectFileIndexStore;
    private readonly UnknownFileStore _unknownFileStore;

    public ProjectFileIndexUpdater(ProjectFileIndexStore projectFileIndexStore, UnknownFileStore unknownFileStore)
    {
        _projectFileIndexStore = projectFileIndexStore ?? throw new ArgumentNullException(nameof(projectFileIndexStore));
        _unknownFileStore = unknownFileStore ?? throw new ArgumentNullException(nameof(unknownFileStore));
    }

    public void UpdateAfterScan(
        ProjectInfo project,
        IEnumerable<string> scannedFolders,
        IEnumerable<IndexedFile> scannedFiles,
        IEnumerable<string>? incompletePaths = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(scannedFolders);
        ArgumentNullException.ThrowIfNull(scannedFiles);

        var scanRoots = scannedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scanRoots.Length == 0)
        {
            throw new ProjectFileIndexException("スキャン範囲のフォルダーがありません。");
        }

        var incompleteScanPaths = (incompletePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingFiles = _projectFileIndexStore.Load(project);
        var existingByPath = existingFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var refreshedFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var scannedFile in scannedFiles)
        {
            if (scannedFile is null || string.IsNullOrWhiteSpace(scannedFile.Path) || !Path.IsPathFullyQualified(scannedFile.Path))
            {
                throw new ProjectFileIndexException("スキャン結果に正しくない絶対パスがあります。");
            }

            var fullPath = Path.GetFullPath(scannedFile.Path);
            refreshedFiles[fullPath] = new IndexedFile
            {
                Path = fullPath,
                Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                SizeBytes = scannedFile.SizeBytes,
                LastModifiedUtc = scannedFile.LastModifiedUtc,
                Tags = existingByPath.TryGetValue(fullPath, out var existingFile)
                    ? [.. existingFile.Tags]
                    : [.. scannedFile.Tags]
            };
        }

        bool IsInScanScope(IndexedFile file)
        {
            return scanRoots.Any(root => IsWithinFolder(file.Path, root));
        }

        bool IsInIncompleteScope(IndexedFile file)
        {
            return incompleteScanPaths.Any(path =>
                StringComparer.OrdinalIgnoreCase.Equals(file.Path, path)
                || IsWithinFolder(file.Path, path));
        }

        var unknownFiles = existingFiles
            .Where(IsInScanScope)
            .Where(file => !IsInIncompleteScope(file))
            .Where(file => !refreshedFiles.ContainsKey(file.Path))
            .Select(file => new UnknownFileEntry
            {
                Path = file.Path,
                Tags = [.. file.Tags]
            })
            .ToArray();

        if (unknownFiles.Length > 0)
        {
            var combinedUnknownFiles = _unknownFileStore.Load().Concat(unknownFiles);
            _unknownFileStore.Save(combinedUnknownFiles);
        }

        var retainedFiles = existingFiles.Where(file => !IsInScanScope(file) || IsInIncompleteScope(file));
        _projectFileIndexStore.Save(project, retainedFiles.Concat(refreshedFiles.Values));
    }

    private static bool IsWithinFolder(string filePath, string folderPath)
    {
        var relativePath = Path.GetRelativePath(folderPath, filePath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".")
        {
            return false;
        }

        return !StringComparer.Ordinal.Equals(relativePath, "..")
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
