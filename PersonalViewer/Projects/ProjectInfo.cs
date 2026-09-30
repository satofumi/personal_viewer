namespace PersonalViewer.Projects;

public sealed class ProjectInfo
{
    public string ProjectId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public List<string> Folders { get; set; } = [];
}
