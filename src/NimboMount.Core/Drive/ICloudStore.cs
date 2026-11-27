namespace NimboMount.Drive;

public interface ICloudStore
{
    string RootHandle { get; }

    long TotalBytes { get; }

    long UsedBytes { get; }

    IReadOnlyList<CloudNode> Snapshot();

    Task ReadAsync(string handle, long offset, byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken);

    Task<CloudNode> CreateFolderAsync(string parentHandle, string name, CancellationToken cancellationToken);

    Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken, Action<long>? onBytesSent = null);

    // Asks MEGA for an upload address for a file of this size. Chunks can then be sent before the file closes.
    Task<IUploadSession> BeginUploadAsync(long size, CancellationToken cancellationToken);

    Task RenameAsync(string handle, string newName, CancellationToken cancellationToken);

    Task MoveAsync(string handle, string newParentHandle, CancellationToken cancellationToken);

    Task DeleteAsync(string handle, CancellationToken cancellationToken);
}

public interface IUploadSession
{
    Task WriteChunkAsync(long offset, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken);

    Task<CloudNode> CompleteAsync(string parentHandle, string name, string? replaceHandle, CancellationToken cancellationToken);

    void Abandon();
}
