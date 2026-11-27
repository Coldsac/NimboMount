using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using Fsp;
using Fsp.Interop;
using NimboMount.Mega;
using FileInfo = Fsp.Interop.FileInfo;

namespace NimboMount.Drive;

public sealed class CloudFileSystem : FileSystemBase
{
    private readonly ICloudStore _store;
    private readonly Action<string>? _log;
    private readonly Action<UploadSample>? _progress;
    private readonly string _workDirectory;
    private readonly string _stagingDirectory;
    private readonly object _tree = new();
    private readonly object _journal = new();
    private readonly ConcurrentDictionary<string, object> _uploadGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _byHandle = new(StringComparer.Ordinal);
    private readonly Entry _root;
    private readonly byte[] _security;
    private string _label = "NimboMount";

    public CloudFileSystem(ICloudStore store, string workDirectory, Action<string>? log = null, Action<UploadSample>? progress = null)
    {
        _store = store;
        _log = log;
        _progress = progress;
        _workDirectory = workDirectory;
        _stagingDirectory = Path.Combine(workDirectory, "staging");
        Directory.CreateDirectory(_stagingDirectory);
        var rootHandle = string.IsNullOrWhiteSpace(store.RootHandle) ? "root" : store.RootHandle;
        _root = new Entry
        {
            Handle = rootHandle,
            RemoteName = "",
            WindowsName = "",
            IsDirectory = true,
            Modified = DateTimeOffset.UtcNow,
        };
        _byHandle[rootHandle] = _root;
        _security = CreateSecurity();
    }

    public bool HasPendingWork
    {
        get
        {
            lock (_tree)
            {
                foreach (var entry in _byHandle.Values)
                {
                    lock (entry.Gate)
                    {
                        if (entry.Opens > 0 || entry.Dirty)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    public void Reload()
    {
        var nodes = _store.Snapshot();
        lock (_tree)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                seen.Add(node.Handle);
                if (!_byHandle.TryGetValue(node.Handle, out var entry))
                {
                    entry = EntryFrom(node);
                    _byHandle[entry.Handle] = entry;
                    continue;
                }

                var keepLocal = false;
                lock (entry.Gate)
                {
                    keepLocal = entry.Dirty || entry.DeletePending;
                    if (!keepLocal)
                    {
                        entry.RemoteName = node.Name;
                        entry.IsDirectory = node.IsDirectory;
                        entry.Modified = node.Modified;
                        if (entry.Opens == 0)
                        {
                            entry.Size = node.Size;
                        }
                    }
                }

                if (!keepLocal)
                {
                    entry.ParentHandle = string.IsNullOrEmpty(node.ParentHandle) ? _root.Handle : node.ParentHandle;
                }
            }

            foreach (var handle in _byHandle.Keys.ToArray())
            {
                if (handle == _root.Handle || seen.Contains(handle) || handle.StartsWith("local:", StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = _byHandle[handle];
                var keep = false;
                lock (entry.Gate)
                {
                    keep = entry.Dirty || entry.Opens > 0 || entry.DeletePending;
                }

                if (!keep)
                {
                    _byHandle.Remove(handle);
                }
            }

            Rebuild();
        }
    }

    public void RecoverInterrupted()
    {
        SweepStagingTemps();
        List<PendingItem> pending;
        lock (_journal)
        {
            pending = ReadJournal();
        }

        foreach (var item in pending)
        {
            if (string.IsNullOrWhiteSpace(item.StagingPath) || !File.Exists(item.StagingPath))
            {
                if (!string.IsNullOrWhiteSpace(item.StagingPath))
                {
                    RemoveJournal(item.StagingPath);
                }

                continue;
            }

            var length = new System.IO.FileInfo(item.StagingPath).Length;
            Report(item.StagingPath, item.RemoteName, 0, length, true);
            try
            {
                try
                {
                    FileRetry.Run(() => Wait(_store.UploadAsync(
                        item.ParentHandle,
                        item.RemoteName,
                        item.StagingPath,
                        item.ReplaceHandle,
                        CancellationToken.None,
                        sent => Report(item.StagingPath, item.RemoteName, sent, length, true))));
                    TryDelete(item.StagingPath);
                    RemoveJournal(item.StagingPath);
                    _log?.Invoke($"Finished an interrupted upload of {item.RemoteName}.");
                }
                catch (Exception ex) when (FileRetry.IsSharingViolation(Unwrap(ex)))
                {
                    _log?.Invoke($"Could not finish the upload of {item.RemoteName} yet. The local copy is still open, so it will be sent the next time the drive mounts.");
                }
                catch (Exception ex)
                {
                    var error = Unwrap(ex);
                    _log?.Invoke($"Could not finish the upload of {item.RemoteName}: {error.Message}");
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
            finally
            {
                Report(item.StagingPath, item.RemoteName, 0, 0, false);
            }
        }

        Reload();
    }

    public Task FlushDirtyAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            Entry[] dirty;
            lock (_tree)
            {
                dirty = _byHandle.Values.Where(entry =>
                {
                    lock (entry.Gate)
                    {
                        return entry.Dirty && !entry.IsDirectory && !entry.DeletePending;
                    }
                }).ToArray();
            }

            foreach (var entry in dirty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int generation;
                bool streaming;
                lock (entry.Gate)
                {
                    generation = entry.Generation;
                    streaming = entry.Opens > 0 && entry.Live != null && !entry.Live.Abandoned;
                }

                // The copy is still writing. Its chunks are already going out, so a second upload would fight over the same file.
                if (streaming)
                {
                    continue;
                }

                Commit(entry, generation, cancellationToken);
            }
        }, cancellationToken);
    }

    public override int ExceptionHandler(Exception ex) => StatusFor(ex);

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = DescribeVolume();
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        if (!string.IsNullOrWhiteSpace(volumeLabel))
        {
            _label = volumeLabel;
        }

        volumeInfo = DescribeVolume();
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        var status = Find(fileName, out var entry);
        if (status != STATUS_SUCCESS)
        {
            return status;
        }

        fileAttributes = Attributes(entry);
        if (securityDescriptor != null)
        {
            securityDescriptor = _security;
        }

        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _security;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor)
    {
        return STATUS_SUCCESS;
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;
        try
        {
            var parts = Split(fileName);
            if (parts.Length == 0 || parts[^1] is "." or "..")
            {
                return STATUS_OBJECT_NAME_INVALID;
            }

            var leaf = parts[^1];
            var parentStatus = Find(ParentOf(fileName), out var parent);
            if (parentStatus == STATUS_OBJECT_NAME_NOT_FOUND)
            {
                return STATUS_OBJECT_PATH_NOT_FOUND;
            }

            if (parentStatus != STATUS_SUCCESS)
            {
                return parentStatus;
            }

            if (!parent.IsDirectory)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            lock (_tree)
            {
                if (parent.DeletePending)
                {
                    return STATUS_DELETE_PENDING;
                }

                if (NameTaken(parent, leaf, null))
                {
                    return STATUS_OBJECT_NAME_COLLISION;
                }
            }

            var directory = (createOptions & FILE_DIRECTORY_FILE) != 0;
            Entry entry;
            if (directory)
            {
                var node = Wait(_store.CreateFolderAsync(parent.Handle, leaf, CancellationToken.None));
                entry = EntryFrom(node);
                entry.ParentHandle = parent.Handle;
                entry.RemoteName = leaf;
                _log?.Invoke($"Created folder {leaf}.");
            }
            else
            {
                var staging = NewStaging();
                using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                {
                }

                entry = new Entry
                {
                    Handle = "local:" + Guid.NewGuid().ToString("n"),
                    ParentHandle = parent.Handle,
                    RemoteName = leaf,
                    IsDirectory = false,
                    Size = 0,
                    Modified = DateTimeOffset.UtcNow,
                    Dirty = true,
                    StagingPath = staging,
                };
            }

            lock (_tree)
            {
                if (NameTaken(parent, leaf, null))
                {
                    if (!directory && entry.StagingPath != null)
                    {
                        TryDelete(entry.StagingPath);
                    }

                    return STATUS_OBJECT_NAME_COLLISION;
                }

                entry.Opens = 1;
                _byHandle[entry.Handle] = entry;
                Rebuild();
                normalizedName = FullPath(entry);
            }

            if (!directory)
            {
                Remember(entry);
            }

            fileNode = entry;
            fileDesc = new OpenDesc { Entry = entry };
            fileInfo = Describe(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return StatusFor(ex);
        }
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;
        var status = Find(fileName, out var entry);
        if (status != STATUS_SUCCESS)
        {
            return status;
        }

        var wantsDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
        var wantsFile = (createOptions & FILE_NON_DIRECTORY_FILE) != 0;
        if (wantsDirectory && !entry.IsDirectory)
        {
            return STATUS_NOT_A_DIRECTORY;
        }

        if (wantsFile && entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        lock (entry.Gate)
        {
            entry.Opens++;
        }

        lock (_tree)
        {
            normalizedName = FullPath(entry);
        }

        fileNode = entry;
        fileDesc = new OpenDesc { Entry = entry };
        fileInfo = Describe(entry);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var entry = (Entry)fileNode;
        if (entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        var status = Materialize(entry, discardCloudContent: true);
        if (status != STATUS_SUCCESS)
        {
            return status;
        }

        fileInfo = Describe(entry);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (fileDesc is not OpenDesc desc)
        {
            return;
        }

        if ((flags & CleanupDelete) != 0)
        {
            try
            {
                DeleteEntry(desc.Entry);
            }
            catch (Exception ex)
            {
                _log?.Invoke(Unwrap(ex).Message);
            }
        }

        Finish(desc);
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is OpenDesc desc)
        {
            Finish(desc);
        }
    }

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var entry = (Entry)fileNode;
        if (entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        if (length == 0)
        {
            return STATUS_SUCCESS;
        }

        try
        {
            long size;
            string? staging;
            lock (entry.Gate)
            {
                size = entry.Size;
                staging = entry.StagingPath;
            }

            if ((long)offset >= size)
            {
                return STATUS_END_OF_FILE;
            }

            var count = (int)Math.Min(length, (ulong)(size - (long)offset));
            var data = new byte[count];
            if (!string.IsNullOrEmpty(staging) && File.Exists(staging))
            {
                using var stream = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Position = (long)offset;
                var read = stream.Read(data, 0, count);
                if (read > 0)
                {
                    Marshal.Copy(data, 0, buffer, read);
                }

                bytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }

            if (entry.Handle.StartsWith("local:", StringComparison.Ordinal))
            {
                return STATUS_END_OF_FILE;
            }

            Wait(_store.ReadAsync(entry.Handle, (long)offset, data, 0, count, CancellationToken.None));
            Marshal.Copy(data, 0, buffer, count);
            bytesTransferred = (uint)count;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return StatusFor(ex);
        }
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var entry = (Entry)fileNode;
        if (entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        try
        {
            if (writeToEndOfFile)
            {
                lock (entry.Gate)
                {
                    offset = (ulong)Math.Max(0, entry.Size);
                }
            }

            long size;
            lock (entry.Gate)
            {
                size = entry.Size;
            }

            if (constrainedIo)
            {
                if ((long)offset >= size)
                {
                    fileInfo = Describe(entry);
                    return STATUS_SUCCESS;
                }

                length = (uint)Math.Min(length, (ulong)(size - (long)offset));
            }

            if (length == 0)
            {
                fileInfo = Describe(entry);
                return STATUS_SUCCESS;
            }

            var end = (long)offset + length;
            if (end > size && !HasRoom(end - size))
            {
                fileInfo = Describe(entry);
                return STATUS_DISK_FULL;
            }

            var status = Materialize(entry, discardCloudContent: false);
            if (status != STATUS_SUCCESS)
            {
                return status;
            }

            var data = new byte[length];
            Marshal.Copy(buffer, data, 0, data.Length);
            LiveUpload? live = null;
            lock (entry.Gate)
            {
                if (string.IsNullOrEmpty(entry.StagingPath))
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }

                using var stream = new FileStream(entry.StagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                if ((long)offset > stream.Length)
                {
                    stream.SetLength((long)offset);
                }

                stream.Position = (long)offset;
                stream.Write(data, 0, data.Length);
                if (end > entry.Size)
                {
                    entry.Size = end;
                }

                NoteWrite(entry, (long)offset, end);
                entry.Dirty = true;
                entry.Generation++;
                entry.Modified = DateTimeOffset.UtcNow;
                live = entry.Live;
            }

            if (live != null)
            {
                Wake(live);
            }

            Remember(entry);
            bytesTransferred = length;
            fileInfo = Describe(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return StatusFor(ex);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = fileNode is Entry entry ? Describe(entry) : default;
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = Describe((Entry)fileNode);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        fileInfo = Describe((Entry)fileNode);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var entry = (Entry)fileNode;
        if (entry.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        try
        {
            long current;
            lock (entry.Gate)
            {
                current = entry.Size;
            }

            if (setAllocationSize && newSize >= (ulong)Math.Max(0, current))
            {
                fileInfo = Describe(entry);
                return STATUS_SUCCESS;
            }

            var target = setAllocationSize ? Math.Min(newSize, (ulong)Math.Max(0, current)) : newSize;
            if (target == (ulong)Math.Max(0, current))
            {
                MaybeStartLive(entry);
                fileInfo = Describe(entry);
                return STATUS_SUCCESS;
            }

            if (target > (ulong)Math.Max(0, current) && !HasRoom((long)target - current))
            {
                return STATUS_DISK_FULL;
            }

            var status = target == 0
                ? Materialize(entry, discardCloudContent: true)
                : Materialize(entry, discardCloudContent: false);
            if (status != STATUS_SUCCESS)
            {
                return status;
            }

            if (target > 0)
            {
                lock (entry.Gate)
                {
                    using var stream = new FileStream(entry.StagingPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    stream.SetLength((long)target);
                    if (entry.Live != null && entry.Live.Target != (long)target)
                    {
                        Abandon(entry, sticky: true);
                    }

                    entry.Size = (long)target;
                    if (entry.Written > entry.Size)
                    {
                        entry.Written = entry.Size;
                    }

                    entry.Dirty = true;
                    entry.Generation++;
                    entry.Modified = DateTimeOffset.UtcNow;
                }

                Remember(entry);
                MaybeStartLive(entry);
            }

            fileInfo = Describe(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return StatusFor(ex);
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var entry = (Entry)fileNode;
        if (!entry.IsDirectory)
        {
            return STATUS_SUCCESS;
        }

        lock (_tree)
        {
            return entry.Children.Count == 0 ? STATUS_SUCCESS : STATUS_DIRECTORY_NOT_EMPTY;
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var entry = (Entry)fileNode;
        try
        {
            var parts = Split(newFileName);
            if (parts.Length == 0 || parts[^1] is "." or "..")
            {
                return STATUS_OBJECT_NAME_INVALID;
            }

            var leaf = parts[^1];
            var parentStatus = Find(ParentOf(newFileName), out var newParent);
            if (parentStatus == STATUS_OBJECT_NAME_NOT_FOUND)
            {
                return STATUS_OBJECT_PATH_NOT_FOUND;
            }

            if (parentStatus != STATUS_SUCCESS)
            {
                return parentStatus;
            }

            if (!newParent.IsDirectory)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            Entry? replaced = null;
            lock (_tree)
            {
                if (newParent.DeletePending)
                {
                    return STATUS_DELETE_PENDING;
                }

                if (NameTaken(newParent, leaf, entry))
                {
                    if (!newParent.Children.TryGetValue(leaf, out var existing))
                    {
                        existing = newParent.Children.Values.First(child =>
                            !ReferenceEquals(child, entry)
                            && (string.Equals(child.RemoteName, leaf, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(child.WindowsName, leaf, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(child.WindowsName, WindowsName.Encode(leaf), StringComparison.OrdinalIgnoreCase)));
                    }

                    if (!replaceIfExists)
                    {
                        return STATUS_OBJECT_NAME_COLLISION;
                    }

                    if (existing.IsDirectory && existing.Children.Count > 0)
                    {
                        return STATUS_DIRECTORY_NOT_EMPTY;
                    }

                    lock (existing.Gate)
                    {
                        if (existing.Opens > 0)
                        {
                            return STATUS_ACCESS_DENIED;
                        }
                    }

                    replaced = existing;
                }
            }

            if (replaced != null)
            {
                DeleteEntry(replaced);
            }

            var oldParent = entry.ParentHandle;
            var oldName = entry.RemoteName;
            lock (_tree)
            {
                entry.ParentHandle = newParent.Handle;
                entry.RemoteName = leaf;
                Rebuild();
            }

            try
            {
                bool dirty;
                lock (entry.Gate)
                {
                    dirty = entry.Dirty;
                }

                if (!dirty && !entry.Handle.StartsWith("local:", StringComparison.Ordinal))
                {
                    if (!string.Equals(oldParent, newParent.Handle, StringComparison.Ordinal))
                    {
                        Wait(_store.MoveAsync(entry.Handle, newParent.Handle, CancellationToken.None));
                    }

                    if (!string.Equals(oldName, leaf, StringComparison.Ordinal))
                    {
                        Wait(_store.RenameAsync(entry.Handle, leaf, CancellationToken.None));
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_tree)
                {
                    entry.ParentHandle = oldParent;
                    entry.RemoteName = oldName;
                    Rebuild();
                }

                return StatusFor(ex);
            }

            Remember(entry);
            _log?.Invoke($"Renamed {oldName} to {leaf}.");
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return StatusFor(ex);
        }
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        var entry = (Entry)fileNode;
        if (context is not DirCursor cursor)
        {
            string[] names;
            lock (_tree)
            {
                var list = new List<string>(entry.Children.Count + 2) { ".", ".." };
                list.AddRange(entry.Children.Keys);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                var start = 0;
                if (!string.IsNullOrEmpty(marker))
                {
                    start = list.FindIndex(name => StringComparer.OrdinalIgnoreCase.Compare(name, marker) > 0);
                    if (start < 0)
                    {
                        start = list.Count;
                    }
                }

                names = list.GetRange(start, list.Count - start).ToArray();
            }

            cursor = new DirCursor { Names = names };
            context = cursor;
        }

        if (cursor.Index >= cursor.Names.Length)
        {
            fileName = null!;
            fileInfo = default;
            return false;
        }

        var current = cursor.Names[cursor.Index++];
        fileName = current;
        if (current == ".")
        {
            fileInfo = Describe(entry);
        }
        else if (current == "..")
        {
            fileInfo = Describe(entry.Parent ?? entry);
        }
        else
        {
            Entry? child;
            lock (_tree)
            {
                entry.Children.TryGetValue(current, out child);
            }

            if (child == null)
            {
                fileName = null!;
                fileInfo = default;
                return false;
            }

            fileInfo = Describe(child);
        }

        return true;
    }

    public override int GetDirInfoByName(
        object fileNode,
        object fileDesc,
        string fileName,
        out string normalizedName,
        out FileInfo fileInfo)
    {
        normalizedName = fileName;
        fileInfo = default;
        var entry = (Entry)fileNode;
        if (fileName == ".")
        {
            fileInfo = Describe(entry);
            return STATUS_SUCCESS;
        }

        if (fileName == "..")
        {
            var parent = entry.Parent ?? entry;
            normalizedName = parent.WindowsName.Length == 0 ? ".." : parent.WindowsName;
            fileInfo = Describe(parent);
            return STATUS_SUCCESS;
        }

        lock (_tree)
        {
            if (!entry.Children.TryGetValue(fileName, out var child))
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            normalizedName = child.WindowsName;
            fileInfo = Describe(child);
            return STATUS_SUCCESS;
        }
    }

    private void Finish(OpenDesc desc)
    {
        if (desc.Cleaned)
        {
            return;
        }

        desc.Cleaned = true;
        var entry = desc.Entry;
        bool upload;
        int generation;
        lock (entry.Gate)
        {
            if (entry.Opens > 0)
            {
                entry.Opens--;
            }

            upload = entry.Opens == 0 && entry.Dirty && !entry.IsDirectory && !entry.DeletePending;
            generation = entry.Generation;
        }

        if (!upload)
        {
            return;
        }

        try
        {
            Commit(entry, generation, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Upload failed for {entry.RemoteName}: {Unwrap(ex).Message}");
        }
    }

    // MEGA needs the final size before the first byte can leave. After that, each full chunk is sent while the copy is still writing.
    private void MaybeStartLive(Entry entry)
    {
        string? id = null;
        string? name = null;
        long total = 0;
        lock (entry.Gate)
        {
            if (entry.IsDirectory || entry.DeletePending || entry.UploadAbandoned || entry.Live != null)
            {
                return;
            }

            if (!entry.Dirty || entry.Size <= 0 || string.IsNullOrEmpty(entry.StagingPath))
            {
                return;
            }

            var upload = new LiveUpload { Target = entry.Size };
            upload.Pump = Task.Run(() => PumpLive(entry, upload));
            entry.Live = upload;
            id = entry.StagingPath ?? entry.Handle;
            name = entry.RemoteName;
            total = entry.Size;
        }

        if (id != null && name != null)
        {
            Report(id, name, 0, total, true);
        }
    }

    private void PumpLive(Entry entry, LiveUpload live)
    {
        try
        {
            var session = Wait(_store.BeginUploadAsync(live.Target, live.Cancel.Token));
            string name;
            lock (entry.Gate)
            {
                if (live.Abandoned || entry.Live != live)
                {
                    session.Abandon();
                    return;
                }

                live.Session = session;
                name = entry.RemoteName;
            }

            _log?.Invoke($"Uploading {name}.");
            while (true)
            {
                byte[]? plain = null;
                long sent = 0;
                lock (entry.Gate)
                {
                    if (live.Abandoned || entry.Live != live || live.Sent >= live.Target)
                    {
                        return;
                    }

                    var length = MegaCrypto.ChunkLength(live.Sent, live.Target);
                    if (length <= 0)
                    {
                        return;
                    }

                    if (ChunkIsReady(entry, live))
                    {
                        if (string.IsNullOrEmpty(entry.StagingPath))
                        {
                            Abandon(entry, sticky: true);
                            return;
                        }

                        sent = live.Sent;
                        live.Reserved = sent + length;
                        plain = new byte[length];
                        using var stream = new FileStream(entry.StagingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                        stream.Position = sent;
                        stream.ReadExactly(plain);
                    }
                }

                if (plain == null)
                {
                    live.Signal.Reset();
                    var proceed = false;
                    lock (entry.Gate)
                    {
                        proceed = live.Abandoned || entry.Live != live || ChunkIsReady(entry, live);
                    }

                    if (!proceed)
                    {
                        live.Signal.Wait(TimeSpan.FromSeconds(2));
                    }

                    continue;
                }

                Wait(session.WriteChunkAsync(sent, plain, live.Cancel.Token));
                string? id = null;
                var reportName = "";
                long sentNow = 0;
                lock (entry.Gate)
                {
                    if (live.Abandoned || entry.Live != live)
                    {
                        return;
                    }

                    live.Sent = sent + plain.Length;
                    id = entry.StagingPath ?? entry.Handle;
                    reportName = entry.RemoteName;
                    sentNow = live.Sent;
                }

                Report(id, reportName, sentNow, live.Target, true);
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                if (live.Abandoned || entry.Live != live)
                {
                    return;
                }

                live.Fault = Unwrap(ex);
                Abandon(entry, sticky: true);
            }
        }
        finally
        {
            live.DisposeSignals();
        }
    }

    private static bool ChunkIsReady(Entry entry, LiveUpload live)
    {
        if (live.Sent >= live.Target)
        {
            return true;
        }

        var length = MegaCrypto.ChunkLength(live.Sent, live.Target);
        if (length <= 0)
        {
            return true;
        }

        var covered = live.Closed ? live.Target : Math.Min(entry.Written, live.Target);
        return live.Sent + length == live.Target ? covered >= live.Target : covered >= live.Sent + length;
    }

    private void NoteWrite(Entry entry, long offset, long end)
    {
        var live = entry.Live;
        if (live != null && !live.Abandoned && (offset < live.Reserved || end > live.Target))
        {
            Abandon(entry, sticky: true);
        }

        if (!entry.UploadAbandoned && offset <= entry.Written && end > entry.Written)
        {
            entry.Written = end;
        }
    }

    private void Abandon(Entry entry, bool sticky)
    {
        if (sticky)
        {
            entry.UploadAbandoned = true;
        }

        var live = entry.Live;
        if (live == null)
        {
            return;
        }

        var id = entry.StagingPath ?? entry.Handle;
        var name = entry.RemoteName;
        live.Abandoned = true;
        entry.Live = null;
        try
        {
            live.Session?.Abandon();
        }
        catch (Exception)
        {
        }

        try
        {
            live.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Wake(live);
        Report(id, name, 0, 0, false);
    }

    private static void Wake(LiveUpload live)
    {
        try
        {
            live.Signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Commit(Entry entry, int generation, CancellationToken cancellationToken)
    {
        LiveUpload? live;
        lock (entry.Gate)
        {
            live = entry.Live;
            if (live != null && !live.Abandoned)
            {
                live.Closed = true;
            }
        }

        if (live != null)
        {
            Wake(live);
            try
            {
                Wait(live.Pump);
            }
            catch (Exception ex)
            {
                lock (entry.Gate)
                {
                    if (entry.Live == live)
                    {
                        live.Fault = Unwrap(ex);
                        Abandon(entry, sticky: true);
                    }
                }
            }

            if (!live.Abandoned && live.Fault == null && live.Session != null && live.Sent == live.Target)
            {
                try
                {
                    if (CompleteLive(entry, live, generation, cancellationToken))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lock (entry.Gate)
                    {
                        if (entry.Live == live)
                        {
                            live.Fault = Unwrap(ex);
                            Abandon(entry, sticky: true);
                        }
                    }
                }
            }
        }

        Upload(entry, generation, cancellationToken);
    }

    private bool CompleteLive(Entry entry, LiveUpload live, int generation, CancellationToken cancellationToken)
    {
        string staging;
        IUploadSession session;
        lock (entry.Gate)
        {
            if (entry.Live != live || live.Abandoned || live.Session == null || string.IsNullOrEmpty(entry.StagingPath))
            {
                return false;
            }

            if (entry.DeletePending || entry.Opens > 0 || entry.Generation != generation)
            {
                Abandon(entry, sticky: true);
                return false;
            }

            staging = entry.StagingPath;
            session = live.Session;
        }

        lock (Gate(staging))
        {
            string parent;
            string name;
            string? replace;
            lock (entry.Gate)
            {
                if (entry.Live != live || live.Abandoned || !entry.Dirty || entry.DeletePending || entry.StagingPath != staging)
                {
                    return false;
                }

                if (entry.Opens > 0 || entry.Generation != generation)
                {
                    Abandon(entry, sticky: true);
                    return false;
                }

                parent = entry.ParentHandle;
                name = entry.RemoteName;
                replace = Replacement(entry);
            }

            var node = Wait(session.CompleteAsync(parent, name, replace, cancellationToken));
            ApplyUploaded(entry, generation, node);
            Report(staging, name, 0, 0, false);
            _log?.Invoke($"Uploaded {name}.");
            return true;
        }
    }

    private void Upload(Entry entry, int generation, CancellationToken cancellationToken)
    {
        string staging;
        lock (entry.Gate)
        {
            if (entry.Live != null && !entry.Live.Abandoned)
            {
                return;
            }

            if (!entry.Dirty || entry.DeletePending || string.IsNullOrEmpty(entry.StagingPath) || !File.Exists(entry.StagingPath))
            {
                return;
            }

            staging = entry.StagingPath;
        }

        // One upload owns this staging file. A flush waits for it instead of opening the same copy.
        lock (Gate(staging))
        {
            UploadLocked(entry, generation, staging, cancellationToken);
        }
    }

    private object Gate(string staging) => _uploadGates.GetOrAdd(staging, _ => new object());

    private void UploadLocked(Entry entry, int generation, string staging, CancellationToken cancellationToken)
    {
        string parent;
        string name;
        string? replace;
        lock (entry.Gate)
        {
            if (!entry.Dirty || entry.DeletePending || entry.StagingPath != staging || !File.Exists(staging))
            {
                return;
            }

            parent = entry.ParentHandle;
            name = entry.RemoteName;
            replace = Replacement(entry);
            generation = entry.Generation;
        }

        var copy = staging + "." + Guid.NewGuid().ToString("n") + ".upload";
        try
        {
            FileRetry.Copy(staging, copy);
            var length = new System.IO.FileInfo(copy).Length;
            Report(staging, name, 0, length, true);
            var node = Wait(_store.UploadAsync(parent, name, copy, replace, cancellationToken, sent => Report(staging, name, sent, length, true)));
            ApplyUploaded(entry, generation, node);
            _log?.Invoke($"Uploaded {name}.");
        }
        finally
        {
            Report(staging, name, 0, 0, false);
            TryDelete(copy);
        }
    }

    private void Report(string? id, string name, long sent, long total, bool active)
    {
        if (_progress == null || string.IsNullOrEmpty(id))
        {
            return;
        }

        try
        {
            _progress(new UploadSample(id, name, sent < 0 ? 0 : sent, total < 0 ? 0 : total, active));
        }
        catch (Exception)
        {
        }
    }

    private void ApplyUploaded(Entry entry, int generation, CloudNode node)
    {
        lock (_tree)
        {
            _byHandle.Remove(entry.Handle);
            entry.Handle = node.Handle;
            _byHandle[entry.Handle] = entry;
        }

        string? discard = null;
        var stillDirty = false;
        lock (entry.Gate)
        {
            if (node.Modified != default)
            {
                entry.Modified = node.Modified;
            }

            if (entry.Generation == generation && !entry.DeletePending)
            {
                entry.Dirty = false;
                entry.Size = node.Size;
                entry.UploadAbandoned = false;
                entry.Live = null;
                if (entry.Opens == 0)
                {
                    discard = entry.StagingPath;
                    entry.StagingPath = null;
                    entry.ReplaceHandle = null;
                    entry.Written = 0;
                }
            }
            else
            {
                stillDirty = entry.Dirty && !entry.DeletePending;
            }
        }

        if (discard != null)
        {
            TryDelete(discard);
            RemoveJournal(discard);
        }
        else if (stillDirty)
        {
            Remember(entry);
        }
    }

    private void DeleteEntry(Entry entry)
    {
        if (entry.IsDirectory)
        {
            lock (_tree)
            {
                if (entry.Children.Count > 0)
                {
                    throw new IOException("That folder is not empty.");
                }
            }
        }

        lock (entry.Gate)
        {
            Abandon(entry, sticky: true);
        }

        string? replace;
        lock (entry.Gate)
        {
            replace = entry.ReplaceHandle;
        }

        if (!entry.Handle.StartsWith("local:", StringComparison.Ordinal))
        {
            Wait(_store.DeleteAsync(entry.Handle, CancellationToken.None));
        }
        else if (!string.IsNullOrEmpty(replace))
        {
            Wait(_store.DeleteAsync(replace, CancellationToken.None));
        }

        string? staging;
        lock (entry.Gate)
        {
            staging = entry.StagingPath;
            entry.StagingPath = null;
            entry.Dirty = false;
            entry.DeletePending = true;
            entry.Generation++;
        }

        lock (_tree)
        {
            _byHandle.Remove(entry.Handle);
            Rebuild();
        }

        if (!string.IsNullOrEmpty(staging))
        {
            TryDelete(staging);
            RemoveJournal(staging);
        }

        _log?.Invoke($"Deleted {entry.RemoteName}.");
    }

    private int Materialize(Entry entry, bool discardCloudContent)
    {
        string? staging;
        long size;
        var handle = entry.Handle;
        lock (entry.Gate)
        {
            staging = entry.StagingPath;
            size = entry.Size;
        }

        if (!string.IsNullOrEmpty(staging) && File.Exists(staging))
        {
            if (!discardCloudContent)
            {
                return STATUS_SUCCESS;
            }

            lock (entry.Gate)
            {
                using var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                entry.Size = 0;
                entry.Written = 0;
                entry.Dirty = true;
                entry.Generation++;
                entry.Modified = DateTimeOffset.UtcNow;
                if (entry.Live != null)
                {
                    Abandon(entry, sticky: true);
                }
            }

            Remember(entry);
            return STATUS_SUCCESS;
        }

        var path = NewStaging();
        try
        {
            if (!discardCloudContent && !handle.StartsWith("local:", StringComparison.Ordinal) && size > 0)
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long offset = 0;
                var buffer = new byte[1024 * 1024];
                while (offset < size)
                {
                    var count = (int)Math.Min(buffer.Length, size - offset);
                    Wait(_store.ReadAsync(handle, offset, buffer, 0, count, CancellationToken.None));
                    stream.Write(buffer, 0, count);
                    offset += count;
                }
            }
            else
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                {
                }
            }
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return StatusFor(ex);
        }

        lock (entry.Gate)
        {
            if (!string.IsNullOrEmpty(entry.StagingPath) && File.Exists(entry.StagingPath))
            {
                TryDelete(path);
            }
            else
            {
                entry.StagingPath = path;
                if (discardCloudContent)
                {
                    entry.Size = 0;
                    entry.Written = 0;
                    entry.Dirty = true;
                    entry.Generation++;
                    entry.Modified = DateTimeOffset.UtcNow;
                    if (entry.Live != null)
                    {
                        Abandon(entry, sticky: true);
                    }
                }
            }
        }

        if (discardCloudContent)
        {
            Remember(entry);
        }

        return STATUS_SUCCESS;
    }

    private int Find(string fileName, out Entry entry)
    {
        var parts = Split(fileName);
        lock (_tree)
        {
            var current = _root;
            for (var i = 0; i < parts.Length; i++)
            {
                if (current.DeletePending)
                {
                    entry = current;
                    return STATUS_DELETE_PENDING;
                }

                if (!current.IsDirectory || !current.Children.TryGetValue(parts[i], out var next))
                {
                    entry = current;
                    return i == parts.Length - 1 && current.IsDirectory
                        ? STATUS_OBJECT_NAME_NOT_FOUND
                        : STATUS_OBJECT_PATH_NOT_FOUND;
                }

                current = next;
            }

            entry = current;
            return current.DeletePending ? STATUS_DELETE_PENDING : STATUS_SUCCESS;
        }
    }

    private void Rebuild()
    {
        foreach (var entry in _byHandle.Values)
        {
            entry.Children.Clear();
        }

        var ordered = _byHandle.Values
            .Where(entry => !ReferenceEquals(entry, _root))
            .OrderBy(entry => entry.ParentHandle, StringComparer.Ordinal)
            .ThenBy(entry => entry.RemoteName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Handle, StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in ordered)
        {
            var parent = ParentEntry(entry);
            entry.Parent = parent;
            entry.WindowsName = NextName(parent, entry.RemoteName);
            parent.Children[entry.WindowsName] = entry;
        }
    }

    private Entry ParentEntry(Entry entry)
    {
        if (string.IsNullOrEmpty(entry.ParentHandle)
            || entry.ParentHandle == entry.Handle
            || !_byHandle.TryGetValue(entry.ParentHandle, out var parent)
            || !parent.IsDirectory
            || ReferenceEquals(parent, entry))
        {
            return _root;
        }

        return parent;
    }

    private static bool NameTaken(Entry parent, string leaf, Entry? self)
    {
        var encoded = WindowsName.Encode(leaf);
        foreach (var child in parent.Children.Values)
        {
            if (ReferenceEquals(child, self))
            {
                continue;
            }

            if (string.Equals(child.RemoteName, leaf, StringComparison.OrdinalIgnoreCase)
                || string.Equals(child.WindowsName, leaf, StringComparison.OrdinalIgnoreCase)
                || string.Equals(child.WindowsName, encoded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NextName(Entry parent, string remoteName)
    {
        var baseName = WindowsName.Encode(remoteName);
        var candidate = baseName;
        var suffix = 2;
        while (parent.Children.ContainsKey(candidate))
        {
            var extra = " (" + suffix++ + ")";
            var trimmed = baseName;
            if (trimmed.Length + extra.Length > 240)
            {
                trimmed = trimmed[..Math.Max(1, 240 - extra.Length)];
            }

            candidate = trimmed + extra;
        }

        return candidate;
    }

    private static string FullPath(Entry entry)
    {
        if (entry.Parent == null)
        {
            return "\\";
        }

        var parent = FullPath(entry.Parent);
        return parent == "\\" ? "\\" + entry.WindowsName : parent + "\\" + entry.WindowsName;
    }

    private FileInfo Describe(Entry entry)
    {
        long size;
        DateTimeOffset modified;
        lock (entry.Gate)
        {
            size = Math.Max(0, entry.Size);
            modified = entry.Modified;
        }

        var time = FileTime(modified);
        var reported = (ulong)size;
        return new FileInfo
        {
            FileAttributes = Attributes(entry),
            AllocationSize = reported == 0 ? 0 : (reported + 4095) & ~4095ul,
            FileSize = reported,
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
            IndexNumber = Index(entry.Handle),
            HardLinks = 1,
        };
    }

    private VolumeInfo DescribeVolume()
    {
        var used = Math.Max(0, _store.UsedBytes);
        var total = Math.Max(0, _store.TotalBytes);
        if (total <= used)
        {
            total = used + (50L * 1024 * 1024 * 1024);
        }

        var info = new VolumeInfo
        {
            TotalSize = (ulong)total,
            FreeSize = (ulong)(total - used),
        };
        info.SetVolumeLabel(_label);
        return info;
    }

    private bool HasRoom(long growth)
    {
        if (growth <= 0)
        {
            return true;
        }

        var total = _store.TotalBytes;
        if (total <= 0)
        {
            return true;
        }

        return growth <= Math.Max(0, total - _store.UsedBytes);
    }

    private void Remember(Entry entry)
    {
        PendingItem? item = null;
        lock (entry.Gate)
        {
            if (!entry.Dirty || string.IsNullOrEmpty(entry.StagingPath))
            {
                return;
            }

            item = new PendingItem
            {
                ParentHandle = entry.ParentHandle,
                RemoteName = entry.RemoteName,
                StagingPath = entry.StagingPath,
                ReplaceHandle = Replacement(entry),
            };
        }

        if (item != null)
        {
            TouchJournal(item);
        }
    }

    private static string? Replacement(Entry entry)
    {
        if (entry.Handle.StartsWith("local:", StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(entry.ReplaceHandle) ? null : entry.ReplaceHandle;
        }

        return entry.Handle;
    }

    private void TouchJournal(PendingItem item)
    {
        lock (_journal)
        {
            var items = ReadJournal();
            var index = items.FindIndex(existing => string.Equals(existing.StagingPath, item.StagingPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                items[index] = item;
            }
            else
            {
                items.Add(item);
            }

            SaveJournal(items);
        }
    }

    private void RemoveJournal(string stagingPath)
    {
        lock (_journal)
        {
            var items = ReadJournal();
            var removed = items.RemoveAll(item => string.Equals(item.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveJournal(items);
            }
        }
    }

    private List<PendingItem> ReadJournal()
    {
        var path = JournalPath;
        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<PendingItem>>(text) ?? [];
    }

    private void SaveJournal(List<PendingItem> items)
    {
        Directory.CreateDirectory(_workDirectory);
        var path = JournalPath;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items));
        File.Move(temp, path, overwrite: true);
    }

    private string JournalPath => Path.Combine(_workDirectory, "pending.json");

    private string NewStaging()
    {
        Directory.CreateDirectory(_stagingDirectory);
        return Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("n"));
    }

    private void SweepStagingTemps()
    {
        if (!Directory.Exists(_stagingDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_stagingDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.Contains(".upload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cipher", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private int StatusFor(Exception ex)
    {
        var error = Unwrap(ex);
        if (error is MegaException mega)
        {
            _log?.Invoke(mega.Message);
            return mega.Code switch
            {
                -17 => STATUS_DISK_FULL,
                -12 => STATUS_OBJECT_NAME_COLLISION,
                -11 or -15 or -16 => STATUS_ACCESS_DENIED,
                -9 => STATUS_OBJECT_NAME_NOT_FOUND,
                _ => STATUS_UNEXPECTED_IO_ERROR,
            };
        }

        _log?.Invoke(error.Message);
        return STATUS_UNEXPECTED_IO_ERROR;
    }

    private static Entry EntryFrom(CloudNode node) => new()
    {
        Handle = node.Handle,
        ParentHandle = node.ParentHandle,
        RemoteName = node.Name,
        IsDirectory = node.IsDirectory,
        Size = node.Size,
        Modified = node.Modified == default ? DateTimeOffset.UtcNow : node.Modified,
    };

    private static uint Attributes(Entry entry) => entry.IsDirectory ? 0x10u : 0x20u;

    private static ulong FileTime(DateTimeOffset when)
    {
        try
        {
            return (ulong)when.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentException)
        {
            return (ulong)DateTime.UtcNow.ToFileTimeUtc();
        }
    }

    private static ulong Index(string handle)
    {
        ulong hash = 14695981039346656037ul;
        foreach (var character in handle)
        {
            hash ^= character;
            hash *= 1099511628211ul;
        }

        return hash == 0 ? 1 : hash;
    }

    private static string[] Split(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName == "\\")
        {
            return [];
        }

        return fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ParentOf(string fileName)
    {
        var parts = Split(fileName);
        return parts.Length <= 1 ? "\\" : "\\" + string.Join('\\', parts[..^1]);
    }

    private static T Wait<T>(Task<T> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo.Capture(Unwrap(ex)).Throw();
            throw;
        }
    }

    private static void Wait(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo.Capture(Unwrap(ex)).Throw();
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 && aggregate.InnerException != null)
        {
            ex = aggregate.InnerException;
        }

        return ex;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] CreateSecurity()
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class LiveUpload
    {
        private readonly object _dispose = new();
        private bool _disposed;

        public required long Target { get; init; }

        public IUploadSession? Session { get; set; }

        public long Sent { get; set; }

        public long Reserved { get; set; }

        public bool Closed { get; set; }

        public bool Abandoned { get; set; }

        public Exception? Fault { get; set; }

        public Task Pump { get; set; } = Task.CompletedTask;

        public CancellationTokenSource Cancel { get; } = new();

        public ManualResetEventSlim Signal { get; } = new(false);

        public void DisposeSignals()
        {
            lock (_dispose)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                Cancel.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                Signal.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class Entry
    {
        public required string Handle { get; set; }

        public string ParentHandle { get; set; } = "";

        public string RemoteName { get; set; } = "";

        public string WindowsName { get; set; } = "";

        public bool IsDirectory { get; set; }

        public long Size { get; set; }

        public DateTimeOffset Modified { get; set; }

        public bool DeletePending { get; set; }

        public int Opens { get; set; }

        public bool Dirty { get; set; }

        public int Generation { get; set; }

        public string? StagingPath { get; set; }

        public string? ReplaceHandle { get; set; }

        public long Written { get; set; }

        public bool UploadAbandoned { get; set; }

        public LiveUpload? Live { get; set; }

        public Entry? Parent { get; set; }

        public Dictionary<string, Entry> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object Gate { get; } = new();
    }

    private sealed class OpenDesc
    {
        public required Entry Entry { get; init; }

        public bool Cleaned { get; set; }
    }

    private sealed class DirCursor
    {
        public required string[] Names { get; init; }

        public int Index { get; set; }
    }

    private sealed class PendingItem
    {
        public string ParentHandle { get; set; } = "";

        public string RemoteName { get; set; } = "";

        public string StagingPath { get; set; } = "";

        public string? ReplaceHandle { get; set; }
    }
}
