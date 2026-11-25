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
            var store = new MemoryStore();
            var fileSystem = new CloudFileSystem(store, root);
            fileSystem.RecoverInterrupted();
            var node = Assert.Single(store.Snapshot(), item => item.Name == "recovered.txt");
            Assert.Equal(9, node.Size);
            Assert.False(File.Exists(staging));
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

file sealed class MemoryStore : ICloudStore
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

    public Task<CloudNode> UploadAsync(string parentHandle, string name, string localPlainPath, string? replaceHandle, CancellationToken cancellationToken)
    {
        var data = File.ReadAllBytes(localPlainPath);
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
            return Task.FromResult(ToNode(item));
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
}
