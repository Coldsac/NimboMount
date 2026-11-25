namespace NimboMount.Drive;

public sealed class CloudNode
{
    public required string Handle { get; init; }

    public required string ParentHandle { get; set; }

    public required string Name { get; set; }

    public required bool IsDirectory { get; init; }

    public long Size { get; set; }

    public DateTimeOffset Modified { get; set; }
}
