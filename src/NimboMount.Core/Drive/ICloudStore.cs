namespace NimboMount.Drive;

public interface ICloudStore
{
    string RootHandle { get; }

    long TotalBytes { get; }

    long UsedBytes { get; }

    IReadOnlyList<CloudNode> Snapshot();

    Task ReadAsync(string handle, long offset, byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken);

    Task<CloudNode> CreateFolderAsync(string parentHandle, string name, CancellationToken cancellationToken);

    Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken);

    Task RenameAsync(string handle, string newName, CancellationToken cancellationToken);

    Task MoveAsync(string handle, string newParentHandle, CancellationToken cancellationToken);

    Task DeleteAsync(string handle, CancellationToken cancellationToken);
}
