using Fsp;

namespace NimboMount.Drive;

public sealed class DriveHost : IDisposable
{
    private FileSystemHost? _host;

    public static bool WinFspInstalled { get; } = File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll");

    public string? MountPoint { get; private set; }

    public bool IsMounted => _host != null;

    public void Mount(CloudFileSystem fileSystem, char letter)
    {
        if (!WinFspInstalled)
        {
            throw new InvalidOperationException("WinFsp is not installed. Install it from https://winfsp.dev/rel/ and then mount again.");
        }

        if (_host != null)
        {
            throw new InvalidOperationException("A drive is already mounted.");
        }

        var host = new FileSystemHost(fileSystem)
        {
            Prefix = "",
            SectorSize = 4096,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength = 255,
            FileInfoTimeout = 1000,
            DirInfoTimeout = 1000,
            VolumeInfoTimeout = 1000,
            FileSystemName = "MEGA",
            VolumeSerialNumber = 0x4E696D62,
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PersistentAcls = false,
            PostCleanupWhenModifiedOnly = false,
            FlushAndPurgeOnCleanup = true,
        };

        var point = char.ToUpperInvariant(letter) + ":";
        var status = host.Preflight(point);
        if (status < 0)
        {
            host.Dispose();
            throw new IOException($"{point} cannot be used for the MEGA drive (WinFsp status {status}). Pick another letter.");
        }

        status = host.MountEx(point, 16, null, false, 0);
        if (status < 0)
        {
            host.Dispose();
            throw new IOException($"WinFsp could not mount {point} (status {status}).");
        }

        _host = host;
        MountPoint = host.MountPoint();
        if (string.IsNullOrEmpty(MountPoint))
        {
            MountPoint = point + "\\";
        }
    }

    public void Unmount()
    {
        var host = Interlocked.Exchange(ref _host, null);
        MountPoint = null;
        if (host == null)
        {
            return;
        }

        try
        {
            host.Unmount();
        }
        finally
        {
            host.Dispose();
        }
    }

    public void Dispose() => Unmount();
}
