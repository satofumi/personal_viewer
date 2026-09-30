using System.IO;
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
            throw new ProjectFileIndexException($"ファイル索引の YAML を解析できません: {indexPath}", exception);
        }

        if (index is null)
        {
            throw new ProjectFileIndexException($"ファイル索引が空です: {indexPath}");
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

    private static string GetIndexPath(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Guid.TryParse(project.ProjectId, out var parsedProjectId))
        {
            throw new ProjectFileIndexException("プロジェクト ID が正しくありません。");
        }

        return Path.Combine(
            SettingsStore.SettingsDirectory,
            "Projects",
            parsedProjectId.ToString("N"),
            "files.yaml");
    }

    private static IReadOnlyList<IndexedFile> Normalize(IEnumerable<IndexedFile>? files, string indexPath)
    {
        if (files is null)
        {
            throw new ProjectFileIndexException($"ファイル索引に files がありません: {indexPath}");
        }

        var uniqueFiles = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path))
            {
                throw new ProjectFileIndexException($"ファイル索引に正しくない絶対パスがあります: {indexPath}");
            }

            var fullPath = Path.GetFullPath(file.Path);
            if (file.SizeBytes < 0)
            {
                throw new ProjectFileIndexException($"ファイル索引に負のサイズがあります: {fullPath}");
            }

            uniqueFiles[fullPath] = new IndexedFile
            {
                Path = fullPath,
                Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                SizeBytes = file.SizeBytes,
                LastModifiedUtc = NormalizeUtc(file.LastModifiedUtc)
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
