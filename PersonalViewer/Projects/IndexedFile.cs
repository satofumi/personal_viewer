namespace PersonalViewer.Projects;

public sealed class IndexedFile
{
    public string Path { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime LastModifiedUtc { get; set; }
}
