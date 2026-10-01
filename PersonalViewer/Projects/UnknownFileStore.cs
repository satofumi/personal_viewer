using System.IO;
using PersonalViewer.Localization;
using System.Text;
using PersonalViewer.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PersonalViewer.Projects;

public sealed class UnknownFileStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<UnknownFileEntry> Load()
    {
        var unknownPath = GetUnknownPath();
        if (!File.Exists(unknownPath))
        {
            return [];
        }

        UnknownFileList? unknownFiles;
        try
        {
            unknownFiles = Deserializer.Deserialize<UnknownFileList>(File.ReadAllText(unknownPath, Encoding.UTF8));
        }
        catch (YamlException exception)
        {
            throw new UnknownFileStoreException(LocalizationService.Format("UnknownYamlParse", unknownPath), exception);
        }

        if (unknownFiles is null)
        {
            throw new UnknownFileStoreException(LocalizationService.Format("UnknownYamlEmpty", unknownPath));
        }

        return Normalize(unknownFiles.Files, unknownPath);
    }

    public void Save(IEnumerable<UnknownFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var unknownPath = GetUnknownPath();
        var normalizedFiles = Normalize(files, unknownPath);
        Directory.CreateDirectory(Path.GetDirectoryName(unknownPath)!);

        var temporaryPath = unknownPath + ".tmp";
        var yaml = Serializer.Serialize(new UnknownFileList
        {
            Files = normalizedFiles.ToList()
        });
        File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, unknownPath, overwrite: true);
    }

    private static string GetUnknownPath()
    {
        return Path.Combine(SettingsStore.SettingsDirectory, "unknown.yaml");
    }

    private static IReadOnlyList<UnknownFileEntry> Normalize(IEnumerable<UnknownFileEntry>? files, string unknownPath)
    {
        if (files is null)
        {
            throw new UnknownFileStoreException(LocalizationService.Format("UnknownFilesMissing", unknownPath));
        }

        var uniqueFiles = new Dictionary<string, UnknownFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path))
            {
                throw new UnknownFileStoreException(LocalizationService.Format("UnknownPathInvalid", unknownPath));
            }

            var fullPath = Path.GetFullPath(file.Path);
            if (!uniqueFiles.TryGetValue(fullPath, out var existing))
            {
                uniqueFiles[fullPath] = new UnknownFileEntry
                {
                    Path = fullPath,
                    Tags = NormalizeTags(file.Tags)
                };
                continue;
            }

            existing.Tags = NormalizeTags(existing.Tags.Concat(file.Tags ?? []));
        }

        return uniqueFiles.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class UnknownFileEntry
{
    public string Path { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];
}

public sealed class UnknownFileList
{
    public List<UnknownFileEntry> Files { get; set; } = [];
}

public sealed class UnknownFileStoreException : Exception
{
    public UnknownFileStoreException(string message) : base(message)
    {
    }

    public UnknownFileStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
