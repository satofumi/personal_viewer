namespace PersonalViewer.Configuration;

public sealed class AppSettings
{
    public List<MediaTypeDefinition> MediaTypes { get; set; } = [];

    public string? LastProjectId { get; set; }
}

public sealed class MediaTypeDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<string> Extensions { get; set; } = [];
}
