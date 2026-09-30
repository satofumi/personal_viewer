using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PersonalViewer.Configuration;

public static class SettingsStore
{
    private const string DefaultSettingsYaml = """
    media_types:
      - name: Video
        extensions:
          - ".mp4"
          - ".m4v"
          - ".mov"
          - ".avi"
          - ".mkv"
          - ".wmv"
          - ".webm"
          - ".mpg"
          - ".mpeg"
      - name: Image
        extensions:
          - ".jpg"
          - ".jpeg"
          - ".png"
          - ".gif"
          - ".bmp"
          - ".tif"
          - ".tiff"
          - ".webp"
    """;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PersonalViewer");

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.yaml");

    public static AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(SettingsDirectory);

        if (!File.Exists(SettingsFilePath))
        {
            File.WriteAllText(SettingsFilePath, DefaultSettingsYaml + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var yaml = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
        AppSettings? settings;
        try
        {
            settings = Deserializer.Deserialize<AppSettings>(yaml);
        }
        catch (YamlException exception)
        {
            throw new SettingsFileException("settings.yaml の YAML を解析できません。", exception);
        }

        if (settings is null)
        {
            throw new SettingsFileException("settings.yaml に設定がありません。");
        }

        ValidateAndNormalize(settings);
        return settings;
    }

    public static void SaveLastProjectId(string? projectId)
    {
        string? normalizedProjectId = null;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            if (!Guid.TryParse(projectId, out var parsedProjectId))
            {
                throw new ArgumentException("プロジェクト ID が正しくありません。", nameof(projectId));
            }

            normalizedProjectId = parsedProjectId.ToString("N");
        }

        var fileBytes = File.ReadAllBytes(SettingsFilePath);
        var hasUtf8Bom = fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF;
        var yaml = File.ReadAllText(SettingsFilePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            var currentSettings = Deserializer.Deserialize<AppSettings>(yaml);
            if (currentSettings is null)
            {
                throw new SettingsFileException("settings.yaml に設定がありません。");
            }

            ValidateAndNormalize(currentSettings);
        }
        catch (YamlException exception)
        {
            throw new SettingsFileException("settings.yaml の YAML を解析できません。", exception);
        }

        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var propertyMatches = Regex.Matches(yaml, @"(?m)^(?:last_project_id|""last_project_id""|'last_project_id')[ \t]*:[^\r\n]*");
        if (propertyMatches.Count > 1)
        {
            throw new SettingsFileException("settings.yaml に last_project_id が重複しています。");
        }

        var value = normalizedProjectId ?? "null";
        if (propertyMatches.Count == 1)
        {
            var match = propertyMatches[0];
            var commentMatch = Regex.Match(match.Value, @"[ \t]+#(?<comment>.*)$");
            var comment = commentMatch.Success ? $"  #{commentMatch.Groups["comment"].Value}" : string.Empty;
            yaml = yaml[..match.Index] + $"last_project_id: {value}{comment}" + yaml[(match.Index + match.Length)..];
        }
        else
        {
            if (yaml.Length > 0 && !yaml.EndsWith('\n'))
            {
                yaml += newline;
            }

            yaml += $"last_project_id: {value}{newline}";
        }

        var temporaryPath = SettingsFilePath + ".tmp";
        File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasUtf8Bom));
        File.Move(temporaryPath, SettingsFilePath, overwrite: true);
    }

    private static void ValidateAndNormalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LastProjectId) || !Guid.TryParse(settings.LastProjectId, out var lastProjectId))
        {
            settings.LastProjectId = null;
        }
        else
        {
            settings.LastProjectId = lastProjectId.ToString("N");
        }

        if (settings.MediaTypes is null || settings.MediaTypes.Count == 0)
        {
            throw new SettingsFileException("settings.yaml に media_types を1件以上定義してください。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mediaType in settings.MediaTypes)
        {
            if (mediaType is null || string.IsNullOrWhiteSpace(mediaType.Name))
            {
                throw new SettingsFileException("各メディア種別には name が必要です。");
            }

            mediaType.Name = mediaType.Name.Trim();
            if (!names.Add(mediaType.Name))
            {
                throw new SettingsFileException($"メディア種別名が重複しています: {mediaType.Name}");
            }

            if (mediaType.Extensions is null || mediaType.Extensions.Count == 0)
            {
                throw new SettingsFileException($"メディア種別 {mediaType.Name} に extensions を1件以上定義してください。");
            }

            var normalizedExtensions = new List<string>();
            foreach (var configuredExtension in mediaType.Extensions)
            {
                if (string.IsNullOrWhiteSpace(configuredExtension))
                {
                    throw new SettingsFileException($"メディア種別 {mediaType.Name} に空の拡張子があります。");
                }

                var extension = configuredExtension.Trim().ToLowerInvariant();
                if (extension.Length < 2 || extension[0] != '.' || extension != Path.GetFileName(extension) || extension.Contains('*') || extension.Contains('?'))
                {
                    throw new SettingsFileException($"拡張子は .mp4 のようにドットから始まる値にしてください: {configuredExtension}");
                }

                if (!normalizedExtensions.Contains(extension, StringComparer.Ordinal))
                {
                    normalizedExtensions.Add(extension);
                }
            }

            mediaType.Extensions = normalizedExtensions;
        }
    }
}

public sealed class SettingsFileException : Exception
{
    public SettingsFileException(string message) : base(message)
    {
    }

    public SettingsFileException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
