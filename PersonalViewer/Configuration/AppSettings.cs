namespace PersonalViewer.Configuration;

public sealed class AppSettings
{
    public List<MediaTypeDefinition> MediaTypes { get; set; } = [];

    public string? LastProjectId { get; set; }

    public string LastViewMode { get; set; } = "details";

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }
}

public sealed class MediaTypeDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<string> Extensions { get; set; } = [];
}
