using System.IO;
using System.Text;
using PersonalViewer.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PersonalViewer.Projects;

public sealed class ProjectStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public string ProjectsDirectory => Path.Combine(SettingsStore.SettingsDirectory, "Projects");

    public ProjectInfo Save(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var normalizedProject = Normalize(project, createIdIfMissing: true);
        var projectDirectory = Path.Combine(ProjectsDirectory, normalizedProject.ProjectId);
        Directory.CreateDirectory(projectDirectory);

        var projectFilePath = Path.Combine(projectDirectory, "project.yaml");
        var temporaryFilePath = Path.Combine(projectDirectory, "project.yaml.tmp");
        var yaml = Serializer.Serialize(normalizedProject);
        File.WriteAllText(temporaryFilePath, yaml + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryFilePath, projectFilePath, overwrite: true);

        return normalizedProject;
    }

    public ProjectInfo Load(string projectId)
    {
        var normalizedId = ParseProjectId(projectId);
        var projectFilePath = Path.Combine(ProjectsDirectory, normalizedId, "project.yaml");
        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("プロジェクト情報が見つかりません。", projectFilePath);
        }

        return ReadProject(projectFilePath, normalizedId);
    }

    public IReadOnlyList<ProjectInfo> LoadAll()
    {
        if (!Directory.Exists(ProjectsDirectory))
        {
            return [];
        }

        var projects = new List<ProjectInfo>();
        foreach (var directory in Directory.EnumerateDirectories(ProjectsDirectory))
        {
            var projectId = Path.GetFileName(directory);
            if (!Guid.TryParseExact(projectId, "N", out _))
            {
                continue;
            }

            var projectFilePath = Path.Combine(directory, "project.yaml");
            if (File.Exists(projectFilePath))
            {
                projects.Add(ReadProject(projectFilePath, projectId));
            }
        }

        return projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ProjectInfo ReadProject(string projectFilePath, string expectedProjectId)
    {
        var yaml = File.ReadAllText(projectFilePath, Encoding.UTF8);
        ProjectInfo? project;
        try
        {
            project = Deserializer.Deserialize<ProjectInfo>(yaml);
        }
        catch (YamlException exception)
        {
            throw new ProjectDataException($"プロジェクト情報の YAML を解析できません: {projectFilePath}", exception);
        }

        if (project is null)
        {
            throw new ProjectDataException($"プロジェクト情報が空です: {projectFilePath}");
        }

        var normalizedProject = Normalize(project, createIdIfMissing: false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(normalizedProject.ProjectId, expectedProjectId))
        {
            throw new ProjectDataException($"プロジェクト ID が保存場所と一致しません: {projectFilePath}");
        }

        return normalizedProject;
    }

    private static ProjectInfo Normalize(ProjectInfo project, bool createIdIfMissing)
    {
        string projectId;
        if (string.IsNullOrWhiteSpace(project.ProjectId) && createIdIfMissing)
        {
            projectId = Guid.NewGuid().ToString("N");
        }
        else if (Guid.TryParse(project.ProjectId, out var parsedProjectId))
        {
            projectId = parsedProjectId.ToString("N");
        }
        else
        {
            throw new ProjectDataException("プロジェクト ID が正しくありません。");
        }

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            throw new ProjectDataException("プロジェクト名を指定してください。");
        }

        if (string.IsNullOrWhiteSpace(project.MediaType))
        {
            throw new ProjectDataException("メディア種別を指定してください。");
        }

        if (project.Folders is null || project.Folders.Count == 0)
        {
            throw new ProjectDataException("プロジェクトには登録フォルダーが最低1つ必要です。");
        }

        var folders = new List<string>();
        foreach (var folder in project.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            {
                throw new ProjectDataException("登録フォルダーには絶対パスを指定してください。");
            }

            folders.Add(Path.GetFullPath(folder));
        }

        return new ProjectInfo
        {
            ProjectId = projectId,
            Name = project.Name.Trim(),
            MediaType = project.MediaType.Trim(),
            Folders = folders
        };
    }

    private static string ParseProjectId(string projectId)
    {
        if (!Guid.TryParse(projectId, out var parsedProjectId))
        {
            throw new ArgumentException("プロジェクト ID が正しくありません。", nameof(projectId));
        }

        return parsedProjectId.ToString("N");
    }
}

public sealed class ProjectDataException : Exception
{
    public ProjectDataException(string message) : base(message)
    {
    }

    public ProjectDataException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
