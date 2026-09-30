using System.IO;
using PersonalViewer.Configuration;

namespace PersonalViewer.Projects;

public sealed class MediaFileScanner
{
    public IReadOnlyList<IndexedFile> ScanFolder(ProjectInfo project, string folderPath, AppSettings settings)
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

    public IReadOnlyList<IndexedFile> Scan(ProjectInfo project, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        var mediaType = settings.MediaTypes.FirstOrDefault(candidate =>
            candidate is not null
            && StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, project.MediaType));
        if (mediaType is null)
        {
            throw new ProjectFileIndexException($"設定にメディア種別がありません: {project.MediaType}");
        }

        if (mediaType.Extensions is null || mediaType.Extensions.Count == 0)
        {
            throw new ProjectFileIndexException($"メディア種別 {mediaType.Name} に拡張子が設定されていません。");
        }

        if (project.Folders is null || project.Folders.Count == 0)
        {
            throw new ProjectFileIndexException("プロジェクトに登録フォルダーがありません。");
        }

        var allowedExtensions = new HashSet<string>(
            mediaType.Extensions
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        var indexedFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in project.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Path.IsPathFullyQualified(folder))
            {
                throw new ProjectFileIndexException("登録フォルダーには絶対パスが必要です。");
            }

            var rootPath = System.IO.Path.GetFullPath(folder);
            foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                var fullPath = System.IO.Path.GetFullPath(filePath);
                var extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
                if (!allowedExtensions.Contains(extension))
                {
                    continue;
                }

                var fileInfo = new FileInfo(fullPath);
                try
                {
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    indexedFiles[fullPath] = new IndexedFile
                    {
                        Path = fullPath,
                        Extension = extension,
                        SizeBytes = fileInfo.Length,
                        LastModifiedUtc = fileInfo.LastWriteTimeUtc
                    };
                }
                catch (FileNotFoundException)
                {
                    // The file may have been removed after directory enumeration.
                }
                catch (DirectoryNotFoundException)
                {
                    // The file's parent directory may have been removed after enumeration.
                }
            }
        }

        return indexedFiles.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
