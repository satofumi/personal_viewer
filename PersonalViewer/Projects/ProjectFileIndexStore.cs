using System.IO;
using PersonalViewer.Localization;
using System.Text;
using PersonalViewer.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PersonalViewer.Projects;

public sealed class ProjectFileIndexStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<IndexedFile> Load(ProjectInfo project)
    {
        var indexPath = GetIndexPath(project);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        ProjectFileIndex? index;
        try
        {
            index = Deserializer.Deserialize<ProjectFileIndex>(File.ReadAllText(indexPath, Encoding.UTF8));
        }
        catch (YamlException exception)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("IndexYamlParse", indexPath), exception);
        }

        if (index is null)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("IndexEmpty", indexPath));
        }

        return Normalize(index.Files, indexPath);
    }

    public void Save(ProjectInfo project, IEnumerable<IndexedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var indexPath = GetIndexPath(project);
        var normalizedFiles = Normalize(files, indexPath);
        var indexDirectory = Path.GetDirectoryName(indexPath)!;
        Directory.CreateDirectory(indexDirectory);

        var temporaryPath = indexPath + ".tmp";
        var yaml = Serializer.Serialize(new ProjectFileIndex
        {
            Files = normalizedFiles.ToList()
        });
        File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, indexPath, overwrite: true);
    }

    public IReadOnlyList<string> LoadTags(ProjectInfo project, string filePath)
    {
        var normalizedPath = NormalizeFilePath(filePath);
        var file = Load(project).FirstOrDefault(indexedFile =>
            StringComparer.OrdinalIgnoreCase.Equals(indexedFile.Path, normalizedPath));
        return file?.Tags.ToArray() ?? [];
    }

    public void SaveTags(ProjectInfo project, string filePath, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        SaveTags(project, new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [filePath] = tags
        });
    }

    public void SaveTags(ProjectInfo project, IReadOnlyDictionary<string, IEnumerable<string>> tagsByFilePath)
    {
        ArgumentNullException.ThrowIfNull(tagsByFilePath);

        var normalizedTagsByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, tags) in tagsByFilePath)
        {
            ArgumentNullException.ThrowIfNull(tags);
            normalizedTagsByPath[NormalizeFilePath(filePath)] = NormalizeTags(tags);
        }

        var files = Load(project).ToList();
        var filesByPath = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var missingPath = normalizedTagsByPath.Keys.FirstOrDefault(path => !filesByPath.ContainsKey(path));
        if (missingPath is not null)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("TagTargetMissing", missingPath));
        }

        foreach (var (path, tags) in normalizedTagsByPath)
        {
            filesByPath[path].Tags = tags;
        }

        Save(project, files);
    }

    private static string GetIndexPath(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Guid.TryParse(project.ProjectId, out var parsedProjectId))
        {
            throw new ProjectFileIndexException(LocalizationService.GetString("InvalidProjectId"));
        }

        return Path.Combine(
            SettingsStore.SettingsDirectory,
            "Projects",
            parsedProjectId.ToString("N"),
            "files.yaml");
    }

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new ProjectFileIndexException(LocalizationService.GetString("TagFileAbsoluteRequired"));
        }

        return Path.GetFullPath(filePath);
    }

    private static IReadOnlyList<IndexedFile> Normalize(IEnumerable<IndexedFile>? files, string indexPath)
    {
        if (files is null)
        {
            throw new ProjectFileIndexException(LocalizationService.Format("IndexFilesMissing", indexPath));
        }

        var uniqueFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path))
            {
                throw new ProjectFileIndexException(LocalizationService.Format("IndexPathInvalid", indexPath));
            }

            var fullPath = Path.GetFullPath(file.Path);
            if (file.SizeBytes < 0)
            {
                throw new ProjectFileIndexException(LocalizationService.Format("IndexNegativeSize", fullPath));
            }

            uniqueFiles[fullPath] = new IndexedFile
            {
                Path = fullPath,
                Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                SizeBytes = file.SizeBytes,
                LastModifiedUtc = NormalizeUtc(file.LastModifiedUtc),
                Tags = NormalizeTags(file.Tags)
            };
        }

        return uniqueFiles.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTime NormalizeUtc(DateTime dateTime)
    {
        return dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class ProjectFileIndex
{
    public List<IndexedFile> Files { get; set; } = [];
}

public sealed class ProjectFileIndexException : Exception
{
    public ProjectFileIndexException(string message) : base(message)
    {
    }

    public ProjectFileIndexException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
