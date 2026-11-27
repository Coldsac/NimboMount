using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NimboMount.Drive;
using Xunit;

namespace NimboMount.Tests;

public class MountTests
{
    [Fact]
    public void InterruptedUploadIsFinishedBeforeTheTreeLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var staging = Path.Combine(root, "staged.bin");
            File.WriteAllBytes(staging, "recovered"u8.ToArray());
            var pending = new[]
            {
                new Dictionary<string, string?>
                {
                    ["ParentHandle"] = "root",
                    ["RemoteName"] = "recovered.txt",
                    ["StagingPath"] = staging,
                    ["ReplaceHandle"] = null,
                },
            };
            File.WriteAllText(Path.Combine(root, "pending.json"), JsonSerializer.Serialize(pending));
            var stagingDirectory = Path.Combine(root, "staging");
            Directory.CreateDirectory(stagingDirectory);
            File.WriteAllBytes(Path.Combine(stagingDirectory, "leftover.upload.cipher"), [1, 2, 3]);
            var store = new MemoryStore();
            var fileSystem = new CloudFileSystem(store, root);
            fileSystem.RecoverInterrupted();
            var node = Assert.Single(store.Snapshot(), item => item.Name == "recovered.txt");
            Assert.Equal(9, node.Size);
            Assert.False(File.Exists(staging));
            Assert.False(File.Exists(Path.Combine(root, "staging", "leftover.upload.cipher")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ALockedUploadDoesNotStopRecovery()
    {
        var previous = FileRetry.SharingBudget;
        FileRetry.SharingBudget = TimeSpan.FromMilliseconds(250);
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var staging = Path.Combine(root, "staged.bin");
            File.WriteAllBytes(staging, "still-here"u8.ToArray());
            File.WriteAllText(Path.Combine(root, "pending.json"), JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, string?>
                {
                    ["ParentHandle"] = "root",
                    ["RemoteName"] = "movie.mkv",
                    ["StagingPath"] = staging,
                    ["ReplaceHandle"] = null,
                },
            }));
            var logs = new List<string>();
            var fileSystem = new CloudFileSystem(new SharingStore(), root, logs.Add);
            fileSystem.RecoverInterrupted();
            Assert.True(File.Exists(staging));
            Assert.Contains(logs, line => line.Contains("still open", StringComparison.Ordinal));
        }
        finally
        {
            FileRetry.SharingBudget = previous;
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ASecondUploadWaitsForTheOneAlreadyRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new BlockingStore();
        var fileSystem = new CloudFileSystem(store, root);
        try
        {
            var status = fileSystem.Create(@"\movie.mkv", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            var bytes = "hello movie"u8.ToArray();
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                status = fileSystem.Write(node, desc, buffer, 0, (uint)bytes.Length, false, false, out var written, out _);
                Assert.Equal(0, status);
                Assert.Equal((uint)bytes.Length, written);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var cleanup = Task.Run(() => fileSystem.Cleanup(node, desc, @"\movie.mkv", 0));
            Assert.True(await Task.WhenAny(store.Entered, Task.Delay(TimeSpan.FromSeconds(5))) == store.Entered);
            var flush = fileSystem.FlushDirtyAsync(CancellationToken.None);
            await Task.Delay(200);
            Assert.Equal(1, store.Calls);
            store.Release();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, store.Calls);
            Assert.Contains(store.Snapshot(), node => node.Name == "movie.mkv" && node.Size == bytes.Length);
        }
        finally
        {
            store.Release();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SharingViolationIsRetriedUntilTheFileIsFree()
    {
        var previous = FileRetry.SharingBudget;
        FileRetry.SharingBudget = TimeSpan.FromSeconds(3);
        var path = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Task<FileStream>? opening = null;
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                opening = Task.Run(() => FileRetry.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None));
                await Task.Delay(400);
                Assert.False(opening.IsCompleted);
            }

            using var opened = await opening.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(opened);
        }
        finally
        {
            FileRetry.SharingBudget = previous;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task UploadStartsWhenTheCopySetsTheFileSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new CountingStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.UploadStarted = _ => started.TrySetResult();
        store.ChunkAccepted = (offset, length) =>
        {
            if (offset == 0)
            {
                first.TrySetResult(length);
            }

            if (offset == 128 * 1024)
            {
                tail.TrySetResult(length);
            }
        };
        var fileSystem = new CloudFileSystem(store, root);
        try
        {
            const int size = 300_000;
            var status = fileSystem.Create(@"\copy.bin", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            status = fileSystem.SetFileSize(node, desc, (ulong)size, false, out _);
            Assert.Equal(0, status);
            Assert.True(await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))) == started.Task);

            WriteAt(fileSystem, node, desc, 0, Pattern(200 * 1024, 0));
            Assert.Equal(128 * 1024, await first.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(store.Snapshot(), item => item.Name == "copy.bin");

            WriteAt(fileSystem, node, desc, 200 * 1024, Pattern(size - (200 * 1024), 200 * 1024));
            Assert.Equal(size - (128 * 1024), await tail.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(store.Snapshot(), item => item.Name == "copy.bin");

            fileSystem.Cleanup(node, desc, @"\copy.bin", 0);
            var saved = Assert.Single(store.Snapshot(), item => item.Name == "copy.bin");
            Assert.Equal(size, saved.Size);
            var back = new byte[size];
            await store.ReadAsync(saved.Handle, 0, back, 0, size, CancellationToken.None);
            Assert.Equal(Pattern(size, 0), back);
            Assert.Equal(0, store.FullUploads);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UploadProgressIsReportedWhileTheCopyIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var started = new TaskCompletionSource<UploadSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunk = new TaskCompletionSource<UploadSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileSystem = new CloudFileSystem(new MemoryStore(), root, null, sample =>
        {
            if (sample.Active && sample.Sent == 0 && sample.Total == 300_000)
            {
                started.TrySetResult(sample);
            }

            if (sample.Active && sample.Sent >= 128 * 1024)
            {
                chunk.TrySetResult(sample);
            }
        });
        try
        {
            const int size = 300_000;
            var status = fileSystem.Create(@"\copy.bin", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            status = fileSystem.SetFileSize(node, desc, (ulong)size, false, out _);
            Assert.Equal(0, status);
            var opened = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("copy.bin", opened.Name);
            Assert.Equal(0, opened.Sent);
            WriteAt(fileSystem, node, desc, 0, Pattern(200 * 1024, 0));
            var sent = await chunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(size, sent.Total);
            Assert.True(sent.Sent < size);
            fileSystem.Cleanup(node, desc, @"\copy.bin", 0);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void AClosedFileReportsBytesSentOnTheFullUpload()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var samples = new List<UploadSample>();
        var fileSystem = new CloudFileSystem(new MemoryStore(), root, null, sample =>
        {
            lock (samples)
            {
                samples.Add(sample);
            }
        });
        try
        {
            var status = fileSystem.Create(@"\note.txt", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            WriteAt(fileSystem, node, desc, 0, "hello"u8.ToArray());
            fileSystem.Cleanup(node, desc, @"\note.txt", 0);
            Assert.Contains(samples, sample => sample.Active && sample.Name == "note.txt" && sample.Sent == 5 && sample.Total == 5);
            Assert.False(samples[^1].Active);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AllocationHintDoesNotStartAnUpload()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new CountingStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.UploadStarted = _ => started.TrySetResult();
        var fileSystem = new CloudFileSystem(store, root);
        try
        {
            var status = fileSystem.Create(@"\copy.bin", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            status = fileSystem.SetFileSize(node, desc, 300_000, true, out _);
            Assert.Equal(0, status);
            await Task.Delay(200);
            Assert.False(started.Task.IsCompleted);
            fileSystem.Cleanup(node, desc, @"\copy.bin", 0);
            Assert.Equal(1, store.FullUploads);
            Assert.Contains(store.Snapshot(), item => item.Name == "copy.bin" && item.Size == 0);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ARewriteOfSentBytesFallsBackToAFullUpload()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var store = new CountingStore();
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ChunkAccepted = (offset, length) =>
        {
            if (offset == 0)
            {
                first.TrySetResult(length);
            }
        };
        var fileSystem = new CloudFileSystem(store, root);
        try
        {
            const int size = 200_000;
            var status = fileSystem.Create(@"\copy.bin", 0, 0, 0, [], 0, out var node, out var desc, out _, out _);
            Assert.Equal(0, status);
            status = fileSystem.SetFileSize(node, desc, (ulong)size, false, out _);
            Assert.Equal(0, status);
            var body = Pattern(size, 3);
            WriteAt(fileSystem, node, desc, 0, body);
            Assert.Equal(128 * 1024, await first.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var rewritten = new byte[32];
            Array.Fill(rewritten, (byte)0x5A);
            WriteAt(fileSystem, node, desc, 0, rewritten);
            fileSystem.Cleanup(node, desc, @"\copy.bin", 0);
            var saved = Assert.Single(store.Snapshot(), item => item.Name == "copy.bin");
            Assert.Equal(size, saved.Size);
            var back = new byte[size];
            await store.ReadAsync(saved.Handle, 0, back, 0, size, CancellationToken.None);
            rewritten.CopyTo(body, 0);
            Assert.Equal(body, back);
            Assert.Equal(1, store.FullUploads);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MountedDriveReadsWritesAndDeletes()
    {
        Assert.True(DriveHost.WinFspInstalled);
        var letter = FreeLetter();
        var root = Path.Combine(Path.GetTempPath(), "nimbomount-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var logs = new List<string>();
        var store = new MemoryStore();
        store.AddFile("root", "hello.txt", Encoding.UTF8.GetBytes("hello mega"));
        store.AddFile("root", "shot:1.txt", Encoding.UTF8.GetBytes("wide"));
        store.AddFile("root", "CON.txt", Encoding.UTF8.GetBytes("device"));
        var fileSystem = new CloudFileSystem(store, root, message =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        });
        fileSystem.Reload();
        using var drive = new DriveHost();
        drive.Mount(fileSystem, letter);
        var mount = letter + @":\";
        try
        {
            Assert.True(Directory.Exists(mount));
            Assert.Equal(DriveType.Fixed, new DriveInfo(letter + ":\\").DriveType);
            Assert.Equal("hello mega", await File.ReadAllTextAsync(mount + "hello.txt"));
            var names = Directory.GetFileSystemEntries(mount).Select(Path.GetFileName).ToArray();
            Assert.Contains(names!, name => name!.Contains('\uFF1A'));
            Assert.Contains(names!, name => name!.Contains('\u200B'));

            Directory.CreateDirectory(mount + "docs");
            var note = mount + @"docs\note.txt";
            await File.WriteAllTextAsync(note, "written");
            Assert.Equal("written", await File.ReadAllTextAsync(note));
            await WaitForAsync(() => store.Snapshot().Any(node => node.Name == "note.txt" && node.Size == 7), logs);

            File.Move(note, mount + @"docs\renamed.txt");
            Assert.True(File.Exists(mount + @"docs\renamed.txt"));
            await WaitForAsync(() => store.Snapshot().Any(node => node.Name == "renamed.txt"), logs);
            File.Delete(mount + @"docs\renamed.txt");
            await WaitForAsync(() => store.Snapshot().All(node => node.Name != "renamed.txt"), logs);
            Assert.False(File.Exists(mount + @"docs\renamed.txt"));
            Directory.Delete(mount + "docs");
            await WaitForAsync(() => store.Snapshot().All(node => node.Name != "docs"), logs);
        }
        catch (Exception ex)
        {
            string detail;
            lock (logs)
            {
                detail = string.Join(Environment.NewLine, logs);
            }

            throw new Xunit.Sdk.XunitException(ex + Environment.NewLine + detail);
        }
        finally
        {
            drive.Unmount();
            TryDeleteDirectory(root);
        }
    }

    private static async Task WaitForAsync(Func<bool> ready, List<string> logs)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        string detail;
        lock (logs)
        {
            detail = string.Join(Environment.NewLine, logs);
        }

        throw new Xunit.Sdk.XunitException("The cloud store did not catch up." + Environment.NewLine + detail);
    }

    private static char FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("No free drive letter.");
    }

    private static void WriteAt(CloudFileSystem fileSystem, object node, object desc, long offset, byte[] bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var status = fileSystem.Write(node, desc, buffer, (ulong)offset, (uint)bytes.Length, false, false, out var written, out _);
            Assert.Equal(0, status);
            Assert.Equal((uint)bytes.Length, written);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((seed + i) * 17);
        }

        return data;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

file class MemoryStore : ICloudStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private int _next = 1;

    public MemoryStore()
    {
        _items["root"] = new Item
        {
            Handle = "root",
            ParentHandle = "",
            Name = "Cloud Drive",
            IsDirectory = true,
        };
    }

    public string RootHandle => "root";

    public long TotalBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    public long UsedBytes { get; private set; }

    public void AddFile(string parent, string name, byte[] data)
    {
        var handle = "f" + _next++;
        lock (_gate)
        {
            _items[handle] = new Item
            {
                Handle = handle,
                ParentHandle = parent,
                Name = name,
                IsDirectory = false,
                Data = data,
            };
            UsedBytes += data.Length;
        }
    }

    public IReadOnlyList<CloudNode> Snapshot()
    {
        lock (_gate)
        {
            return _items.Values
                .Where(item => item.Handle != "root")
                .Select(ToNode)
                .ToArray();
        }
    }

    public Task ReadAsync(string handle, long offset, byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(handle, out var item) || offset >= item.Data.Length || count <= 0)
            {
                return Task.CompletedTask;
            }

            var available = (int)Math.Min(count, item.Data.Length - offset);
            Buffer.BlockCopy(item.Data, (int)offset, buffer, bufferOffset, available);
        }

        return Task.CompletedTask;
    }

    public Task<CloudNode> CreateFolderAsync(string parentHandle, string name, CancellationToken cancellationToken)
    {
        var handle = "d" + _next++;
        var item = new Item
        {
            Handle = handle,
            ParentHandle = parentHandle,
            Name = name,
            IsDirectory = true,
        };
        lock (_gate)
        {
            _items[handle] = item;
        }

        return Task.FromResult(ToNode(item));
    }

    public Action<long>? UploadStarted { get; set; }

    public Action<long, int>? ChunkAccepted { get; set; }

    public virtual Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken, Action<long>? onBytesSent = null)
    {
        var data = File.ReadAllBytes(localPlainPath);
        onBytesSent?.Invoke(data.Length);
        return Task.FromResult(Accept(parentHandle, name, data, replaceHandle));
    }

    public virtual Task<IUploadSession> BeginUploadAsync(long size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (size <= 0 || size > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        UploadStarted?.Invoke(size);
        return Task.FromResult<IUploadSession>(new MemoryUpload(this, (int)size));
    }

    private CloudNode Accept(string parentHandle, string name, byte[] data, string? replaceHandle)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(replaceHandle) && _items.Remove(replaceHandle, out var previous) && !previous.IsDirectory)
            {
                UsedBytes = Math.Max(0, UsedBytes - previous.Data.Length);
            }

            var handle = "f" + _next++;
            var item = new Item
            {
                Handle = handle,
                ParentHandle = parentHandle,
                Name = name,
                IsDirectory = false,
                Data = data,
            };
            _items[handle] = item;
            UsedBytes += data.Length;
            return ToNode(item);
        }
    }

    public Task RenameAsync(string handle, string newName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(handle, out var item))
            {
                item.Name = newName;
            }
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string handle, string newParentHandle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(handle, out var item))
            {
                item.ParentHandle = newParentHandle;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string handle, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Remove(handle);
        }

        return Task.CompletedTask;
    }

    private void Remove(string handle)
    {
        if (!_items.Remove(handle, out var item))
        {
            return;
        }

        if (!item.IsDirectory)
        {
            UsedBytes = Math.Max(0, UsedBytes - item.Data.Length);
        }

        foreach (var child in _items.Values.Where(candidate => candidate.ParentHandle == handle).Select(candidate => candidate.Handle).ToArray())
        {
            Remove(child);
        }
    }

    private static CloudNode ToNode(Item item) => new()
    {
        Handle = item.Handle,
        ParentHandle = item.ParentHandle,
        Name = item.Name,
        IsDirectory = item.IsDirectory,
        Size = item.IsDirectory ? 0 : item.Data.Length,
        Modified = DateTimeOffset.UtcNow,
    };

    private sealed class Item
    {
        public string Handle { get; set; } = "";

        public string ParentHandle { get; set; } = "";

        public string Name { get; set; } = "";

        public bool IsDirectory { get; set; }

        public byte[] Data { get; set; } = [];
    }

    private sealed class MemoryUpload : IUploadSession
    {
        private readonly MemoryStore _store;
        private readonly byte[] _data;
        private readonly object _gate = new();
        private long _next;
        private bool _abandoned;
        private bool _done;

        public MemoryUpload(MemoryStore store, int size)
        {
            _store = store;
            _data = new byte[size];
        }

        public Task WriteChunkAsync(long offset, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_abandoned || _done)
                {
                    throw new InvalidOperationException("The upload was cancelled.");
                }

                if (offset != _next || offset + plaintext.Length > _data.Length)
                {
                    throw new InvalidOperationException("Upload chunk is out of order.");
                }

                plaintext.Span.CopyTo(_data.AsSpan((int)offset));
                _next += plaintext.Length;
            }

            _store.ChunkAccepted?.Invoke(offset, plaintext.Length);
            return Task.CompletedTask;
        }

        public Task<CloudNode> CompleteAsync(string parentHandle, string name, string? replaceHandle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            lock (_gate)
            {
                if (_abandoned || _done || _next != _data.Length)
                {
                    throw new InvalidOperationException("The upload is not finished.");
                }

                _done = true;
                data = _data.ToArray();
            }

            return Task.FromResult(_store.Accept(parentHandle, name, data, replaceHandle));
        }

        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
            }
        }
    }
}

file sealed class SharingStore : MemoryStore
{
    public override Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken, Action<long>? onBytesSent = null)
    {
        throw new IOException("The process cannot access the file because it is being used by another process.")
        {
            HResult = unchecked((int)0x80070020),
        };
    }
}

file sealed class CountingStore : MemoryStore
{
    public int FullUploads;

    public override Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken, Action<long>? onBytesSent = null)
    {
        Interlocked.Increment(ref FullUploads);
        return base.UploadAsync(parentHandle, name, localPlainPath, replaceHandle, cancellationToken, onBytesSent);
    }
}

file sealed class BlockingStore : MemoryStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public Task Entered => _entered.Task;

    public int Calls => _calls;

    public void Release() => _release.TrySetResult();

    public override async Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken, Action<long>? onBytesSent = null)
    {
        Interlocked.Increment(ref _calls);
        _entered.TrySetResult();
        await _release.Task;
        return await base.UploadAsync(parentHandle, name, localPlainPath, replaceHandle, cancellationToken, onBytesSent);
    }
}
