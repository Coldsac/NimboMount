using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NimboMount.Drive;

namespace NimboMount.Mega;

public sealed class MegaSession
{
    public required string Email { get; init; }

    public required string UserHandle { get; init; }

    public required string SessionId { get; init; }

    public required byte[] MasterKey { get; init; }
}

public sealed class MegaClient : ICloudStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _shareKeys = new(StringComparer.Ordinal);
    private int _sequence = Random.Shared.Next(1000, 9000);
    private string? _sid;
    private byte[]? _masterKey;
    private BigIntegerPair? _rsa;
    private string? _user;

    public MegaClient(Action<string>? log = null)
    {
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NimboMount/1.0");
    }

    public string? Email { get; private set; }

    public string? UserHandle => _user;

    public string RootHandle { get; private set; } = "";

    public long TotalBytes { get; private set; }

    public long UsedBytes { get; private set; }

    public int SkippedItems { get; private set; }

    public bool IsConnected => _sid != null && _masterKey != null;

    public MegaSession ExportSession()
    {
        if (_sid == null || _masterKey == null || _user == null || Email == null)
        {
            throw new MegaException("Sign in before saving a session.");
        }

        return new MegaSession
        {
            Email = Email,
            UserHandle = _user,
            SessionId = _sid,
            MasterKey = _masterKey.ToArray(),
        };
    }

    public void Resume(MegaSession session)
    {
        Email = session.Email;
        _user = session.UserHandle;
        _sid = session.SessionId;
        _masterKey = session.MasterKey.ToArray();
        _rsa = null;
    }

    public async Task LoginAsync(string email, string password, string? mfa, CancellationToken cancellationToken)
    {
        email = email.Trim().ToLowerInvariant();
        var pre = await InvokeAsync(new JsonObject { ["a"] = "us0", ["user"] = email }, cancellationToken).ConfigureAwait(false);
        var version = pre.TryGetProperty("v", out var versionValue) ? versionValue.GetInt32() : 1;
        byte[] passwordKey;
        string userHash;
        if (version >= 2)
        {
            if (!pre.TryGetProperty("s", out var saltValue) || saltValue.GetString() is not { } saltText)
            {
                throw new MegaException("MEGA did not return an account salt.");
            }

            var derived = MegaCrypto.DerivePasswordKeyV2(password, MegaCodec.FromBase64(saltText));
            passwordKey = derived[..16];
            userHash = MegaCodec.ToBase64(derived.AsSpan(16, 16));
        }
        else
        {
            _log?.Invoke("Unlocking a legacy MEGA password. This can take a few seconds.");
            passwordKey = MegaCrypto.DerivePasswordKeyV1(password);
            userHash = MegaCrypto.UserHashV1(email, passwordKey);
        }

        var login = new JsonObject
        {
            ["a"] = "us",
            ["user"] = email,
            ["uh"] = userHash,
        };
        if (!string.IsNullOrWhiteSpace(mfa))
        {
            login["mfa"] = mfa.Trim();
        }

        var response = await InvokeAsync(login, cancellationToken).ConfigureAwait(false);
        var master = MegaCrypto.AesEcbCrypt(Align(MegaCodec.FromBase64(Required(response, "k"))), passwordKey, encrypt: false);
        if (master.Length < 16)
        {
            throw new MegaException("That password did not unlock this MEGA account.");
        }

        master = master[..16];
        var user = Required(response, "u");
        string sid;
        BigIntegerPair? rsa = null;
        if (response.TryGetProperty("tsid", out var temporary) && temporary.GetString() is { } temporarySid)
        {
            sid = temporarySid;
        }
        else
        {
            var privateBlob = MegaCrypto.AesEcbCrypt(Align(MegaCodec.FromBase64(Required(response, "privk"))), master, encrypt: false);
            var parts = MegaCrypto.ReadMpis(privateBlob, 4);
            if (parts.Count < 4
                || parts[1].GetBitLength() <= 1000
                || parts[0].GetBitLength() <= 1000
                || parts[2].GetBitLength() <= 2000)
            {
                throw new MegaException("That password did not unlock this MEGA account.");
            }

            var q = parts[0];
            var p = parts[1];
            var d = parts[2];
            var u = parts[3];
            if (MegaCrypto.ModInverse(q, p) != u)
            {
                throw new MegaException("That password did not unlock this MEGA account.");
            }

            var sessionPlain = MegaCrypto.RsaRawDecrypt(MegaCodec.FromBase64(Required(response, "csid")), d, p * q);
            if (sessionPlain.Length < 27)
            {
                throw new MegaException("That password did not unlock this MEGA account.");
            }

            var embeddedUser = Encoding.ASCII.GetString(sessionPlain, 16, 11);
            if (!string.Equals(embeddedUser, user, StringComparison.Ordinal))
            {
                throw new MegaException("That password did not unlock this MEGA account.");
            }

            sid = MegaCodec.ToBase64(sessionPlain.AsSpan(0, 43));
            rsa = new BigIntegerPair(d, p * q);
        }

        Email = email;
        _user = user;
        _sid = sid;
        _masterKey = master;
        _rsa = rsa;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        var response = await InvokeAsync(new JsonObject { ["a"] = "f", ["c"] = 1 }, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("f", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new MegaException("MEGA did not return a file list.");
        }

        var raw = new List<RemoteNode>();
        foreach (var item in files.EnumerateArray())
        {
            if (!item.TryGetProperty("h", out var handleValue) || handleValue.GetString() is not { } handle)
            {
                continue;
            }

            raw.Add(new RemoteNode
            {
                Handle = handle,
                ParentHandle = item.TryGetProperty("p", out var parent) ? parent.GetString() : null,
                Type = item.TryGetProperty("t", out var type) ? type.GetInt32() : 0,
                Size = item.TryGetProperty("s", out var size) && size.TryGetInt64(out var parsed) ? parsed : 0,
                Modified = item.TryGetProperty("ts", out var stamp) && stamp.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : DateTimeOffset.UnixEpoch,
                KeyField = item.TryGetProperty("k", out var key) ? key.GetString() : null,
                AttributeField = item.TryGetProperty("a", out var attributes) ? attributes.GetString() : null,
                ShareKeyField = item.TryGetProperty("sk", out var share) ? share.GetString() : null,
            });
        }

        LoadShareKeys(response, raw);
        DecryptNodes(raw);
        var visible = BuildVisible(raw);
        lock (_gate)
        {
            _nodes.Clear();
            foreach (var node in visible)
            {
                _nodes[node.Handle] = node;
            }

            RootHandle = raw.FirstOrDefault(node => node.Type == 2)?.Handle ?? "";
            SkippedItems = raw.Count(node => node.Type is 0 or 1 && !node.Unlocked);
        }

        try
        {
            await RefreshQuotaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MegaException ex)
        {
            _log?.Invoke(ex.Message);
            if (TotalBytes <= UsedBytes)
            {
                TotalBytes = UsedBytes + (50L * 1024 * 1024 * 1024);
            }
        }

        var filesCount = visible.Count(node => !node.IsDirectory);
        var folders = visible.Count(node => node.IsDirectory && node.Type == 1);
        _log?.Invoke($"Cloud Drive is ready. {folders:N0} folders, {filesCount:N0} files.");
        if (SkippedItems > 0)
        {
            _log?.Invoke($"{SkippedItems:N0} items were skipped because their keys could not be opened.");
        }
    }

    public async Task RefreshQuotaAsync(CancellationToken cancellationToken)
    {
        var quota = await InvokeAsync(new JsonObject { ["a"] = "uq", ["strg"] = 1, ["xfer"] = 1 }, cancellationToken).ConfigureAwait(false);
        if (quota.TryGetProperty("mstrg", out var total) && total.TryGetInt64(out var totalBytes))
        {
            TotalBytes = totalBytes;
        }

        if (quota.TryGetProperty("cstrg", out var used) && used.TryGetInt64(out var usedBytes))
        {
            UsedBytes = usedBytes;
        }
    }

    public IReadOnlyList<CloudNode> Snapshot()
    {
        lock (_gate)
        {
            return _nodes.Values
                .Where(node => node.Type is 0 or 1)
                .Select(node => new CloudNode
                {
                    Handle = node.Handle,
                    ParentHandle = node.DisplayParent ?? node.ParentHandle ?? RootHandle ?? "",
                    Name = node.Name ?? node.Handle,
                    IsDirectory = node.IsDirectory,
                    Size = node.Size,
                    Modified = node.Modified,
                })
                .ToArray();
        }
    }

    public async Task ReadAsync(string handle, long offset, byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken)
    {
        RemoteNode node;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(handle, out node!))
            {
                throw new MegaException("That file is no longer in the cloud drive.", -9);
            }
        }

        if (node.AesKey == null || node.Nonce == null)
        {
            throw new MegaException("This file has no readable key.");
        }

        if (count <= 0 || offset >= node.Size)
        {
            return;
        }

        count = (int)Math.Min(count, node.Size - offset);
        const int blockSize = 1024 * 1024;
        var done = 0;
        while (done < count)
        {
            var position = offset + done;
            var blockIndex = position / blockSize;
            var blockStart = blockIndex * blockSize;
            var blockLength = (int)Math.Min(blockSize, node.Size - blockStart);
            var plain = await LoadBlockAsync(node, blockStart, blockLength, cancellationToken).ConfigureAwait(false);
            var inside = (int)(position - blockStart);
            var copy = Math.Min(plain.Length - inside, count - done);
            Buffer.BlockCopy(plain, inside, buffer, bufferOffset + done, copy);
            done += copy;
        }
    }

    public async Task<CloudNode> CreateFolderAsync(string parentHandle, string name, CancellationToken cancellationToken)
    {
        EnsureReady();
        var key = MegaCrypto.Random(16);
        var request = FolderPutRequest(
            parentHandle,
            MegaCodec.ToBase64(MegaCrypto.EncryptAttributes(name, key)),
            MegaCodec.ToBase64(MegaCrypto.AesEcbCrypt(key, _masterKey!, encrypt: true)));
        var response = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        var created = ReadCreatedNode(response, parentHandle, name, isDirectory: true, size: 0);
        Remember(created, key, folder: true);
        return ToCloud(created);
    }

    public async Task<CloudNode> UploadAsync(
        string parentHandle,
        string name,
        string plainPath,
        string? replaceHandle,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        var length = new FileInfo(plainPath).Length;
        long replaced = 0;
        if (!string.IsNullOrEmpty(replaceHandle))
        {
            lock (_gate)
            {
                if (_nodes.TryGetValue(replaceHandle, out var previous))
                {
                    replaced = previous.Size;
                }
            }
        }
        var aesKey = MegaCrypto.Random(16);
        var nonce = MegaCrypto.Random(8);
        var cipherPath = plainPath + ".cipher";
        try
        {
            await using (var input = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            await using (var output = new FileStream(cipherPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long offset = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    MegaCrypto.CryptCtr(buffer.AsSpan(0, read), aesKey, nonce, offset);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    offset += read;
                }
            }

            byte[] mac;
            await using (var cipher = new FileStream(cipherPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                mac = MegaCrypto.ChunkMac(cipher, aesKey, nonce);
            }

            var fileKey = MegaCrypto.PackFileKey(aesKey, nonce, mac);
            var node = new JsonObject
            {
                ["t"] = 0,
                ["h"] = await PostFileAsync(cipherPath, length, cancellationToken).ConfigureAwait(false),
                ["a"] = MegaCodec.ToBase64(MegaCrypto.EncryptAttributes(name, aesKey)),
                ["k"] = MegaCodec.ToBase64(MegaCrypto.AesEcbCrypt(fileKey, _masterKey!, encrypt: true)),
            };
            if (!string.IsNullOrEmpty(replaceHandle))
            {
                node["ov"] = replaceHandle;
            }

            var response = await InvokeAsync(PutRequest(parentHandle, node), cancellationToken).ConfigureAwait(false);
            var created = ReadCreatedNode(response, parentHandle, name, isDirectory: false, length);
            Remember(created, fileKey, folder: false);
            if (!string.IsNullOrEmpty(replaceHandle) && replaceHandle != created.Handle)
            {
                lock (_gate)
                {
                    _nodes.Remove(replaceHandle);
                }
            }

            TryDelete(Path.Combine(CacheRoot, created.Handle));
            UsedBytes = Math.Max(0, UsedBytes - replaced + length);
            return ToCloud(created);
        }
        finally
        {
            TryDelete(cipherPath);
        }
    }

    public async Task RenameAsync(string handle, string name, CancellationToken cancellationToken)
    {
        RemoteNode node;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(handle, out node!))
            {
                throw new MegaException("That item is no longer in the cloud drive.", -9);
            }
        }

        var attributeKey = node.IsDirectory ? node.RawKey : node.AesKey;
        if (attributeKey == null)
        {
            throw new MegaException("This item has no key, so it cannot be renamed.");
        }

        await InvokeAsync(new JsonObject
        {
            ["a"] = "a",
            ["n"] = handle,
            ["at"] = MegaCodec.ToBase64(MegaCrypto.EncryptAttributes(name, attributeKey)),
        }, cancellationToken).ConfigureAwait(false);
        node.Name = name;
    }

    public async Task MoveAsync(string handle, string newParent, CancellationToken cancellationToken)
    {
        await InvokeAsync(new JsonObject
        {
            ["a"] = "m",
            ["n"] = handle,
            ["t"] = newParent,
        }, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_nodes.TryGetValue(handle, out var node))
            {
                node.ParentHandle = newParent;
                node.DisplayParent = newParent;
            }
        }
    }

    public async Task DeleteAsync(string handle, CancellationToken cancellationToken)
    {
        long size;
        lock (_gate)
        {
            size = _nodes.TryGetValue(handle, out var node) ? node.Size : 0;
        }

        await InvokeAsync(new JsonObject { ["a"] = "d", ["n"] = handle }, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            RemoveTree(handle);
        }

        UsedBytes = Math.Max(0, UsedBytes - size);
    }

    public void Dispose() => _http.Dispose();

    private async Task<string> PostFileAsync(string cipherPath, long length, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(new JsonObject { ["a"] = "u", ["s"] = length, ["ssl"] = 2 }, cancellationToken).ConfigureAwait(false);
        var url = response.ValueKind == JsonValueKind.String
            ? response.GetString()
            : response.TryGetProperty("p", out var post) ? post.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new MegaException("MEGA did not return an upload address.");
        }

        await using var stream = new FileStream(cipherPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var posted = await _http.PostAsync(url.TrimEnd('/') + "/0", content, cancellationToken).ConfigureAwait(false);
        var body = (await posted.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (!posted.IsSuccessStatusCode || body.Length == 0 || body.StartsWith('-') || body.StartsWith('<'))
        {
            throw new MegaException("MEGA did not accept the upload.");
        }

        if (body.StartsWith('"'))
        {
            return JsonSerializer.Deserialize<string>(body) ?? throw new MegaException("MEGA did not accept the upload.");
        }

        return body;
    }

    private async Task<byte[]> LoadBlockAsync(RemoteNode node, long start, int length, CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(CacheRoot, node.Handle);
        Directory.CreateDirectory(cacheDir);
        var cacheFile = Path.Combine(cacheDir, $"{start:x12}.bin");
        if (File.Exists(cacheFile))
        {
            var cached = await File.ReadAllBytesAsync(cacheFile, cancellationToken).ConfigureAwait(false);
            if (cached.Length == length)
            {
                return cached;
            }
        }

        await node.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = await File.ReadAllBytesAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                if (cached.Length == length)
                {
                    return cached;
                }
            }

            var url = await DownloadUrlAsync(node, cancellationToken).ConfigureAwait(false);
            var end = start + length - 1;
            using var response = await _http.GetAsync($"{url}/{start}-{end}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                node.DownloadUrl = null;
                url = await DownloadUrlAsync(node, cancellationToken).ConfigureAwait(false);
                using var retry = await _http.GetAsync($"{url}/{start}-{end}", cancellationToken).ConfigureAwait(false);
                retry.EnsureSuccessStatusCode();
                var cipherRetry = await retry.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                MegaCrypto.CryptCtr(cipherRetry, node.AesKey!, node.Nonce!, start);
                await File.WriteAllBytesAsync(cacheFile, cipherRetry, cancellationToken).ConfigureAwait(false);
                return cipherRetry;
            }

            var cipher = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (cipher.Length < length)
            {
                throw new MegaException("MEGA returned a short read.");
            }

            if (cipher.Length > length)
            {
                cipher = cipher[..length];
            }

            MegaCrypto.CryptCtr(cipher, node.AesKey!, node.Nonce!, start);
            await File.WriteAllBytesAsync(cacheFile, cipher, cancellationToken).ConfigureAwait(false);
            return cipher;
        }
        finally
        {
            node.Gate.Release();
        }
    }

    private async Task<string> DownloadUrlAsync(RemoteNode node, CancellationToken cancellationToken)
    {
        if (node.DownloadUrl != null && DateTimeOffset.UtcNow < node.DownloadExpires)
        {
            return node.DownloadUrl;
        }

        var response = await InvokeAsync(new JsonObject
        {
            ["a"] = "g",
            ["g"] = 1,
            ["ssl"] = 2,
            ["n"] = node.Handle,
        }, cancellationToken).ConfigureAwait(false);
        var url = response.TryGetProperty("g", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new MegaException("MEGA did not return a download address.");
        }

        node.DownloadUrl = url;
        node.DownloadExpires = DateTimeOffset.UtcNow.AddMinutes(4);
        return url;
    }

    private void LoadShareKeys(JsonElement response, List<RemoteNode> nodes)
    {
        _shareKeys.Clear();
        if (_masterKey == null)
        {
            return;
        }

        if (response.TryGetProperty("ok", out var ownerKeys) && ownerKeys.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ownerKeys.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("h", out var handleValue)
                    || handleValue.GetString() is not { } handle
                    || !item.TryGetProperty("k", out var keyValue)
                    || keyValue.GetString() is not { } keyText)
                {
                    continue;
                }

                RememberShare(handle, keyText);
            }
        }

        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.ShareKeyField))
            {
                RememberShare(node.Handle, node.ShareKeyField);
            }
        }
    }

    private void RememberShare(string handle, string keyText)
    {
        try
        {
            var cipher = MegaCodec.FromBase64(keyText);
            byte[] plain;
            if (cipher.Length > 32 && _rsa is { } shareRsa)
            {
                plain = MegaCrypto.RsaRawDecrypt(cipher, shareRsa.Exponent, shareRsa.Modulus);
            }
            else
            {
                plain = MegaCrypto.AesEcbCrypt(Align(cipher), _masterKey!, encrypt: false);
            }

            if (plain.Length >= 16 && !plain.AsSpan(0, 16).SequenceEqual(new byte[16]))
            {
                _shareKeys[handle] = plain[..16];
            }
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or MegaException)
        {
            _log?.Invoke($"A share key for {handle} could not be opened.");
        }
    }

    private void DecryptNodes(List<RemoteNode> nodes)
    {
        var pending = new List<RemoteNode>(nodes);
        var guard = 0;
        while (pending.Count > 0 && guard++ < 8)
        {
            var next = new List<RemoteNode>();
            foreach (var node in pending)
            {
                if (node.Type is 2 or 3 or 4)
                {
                    node.Unlocked = true;
                    node.Name = node.Type switch
                    {
                        2 => "Cloud Drive",
                        3 => "Inbox",
                        _ => "Rubbish Bin",
                    };
                    continue;
                }

                if (TryUnlock(node))
                {
                    if (node.IsDirectory && node.RawKey != null)
                    {
                        _shareKeys[node.Handle] = node.RawKey.AsSpan(0, 16).ToArray();
                    }
                }
                else
                {
                    next.Add(node);
                }
            }

            if (next.Count == pending.Count)
            {
                break;
            }

            pending = next;
        }
    }

    private bool TryUnlock(RemoteNode node)
    {
        if (string.IsNullOrEmpty(node.KeyField) || string.IsNullOrEmpty(node.AttributeField))
        {
            return false;
        }

        foreach (var (id, cipher) in SplitKeys(node.KeyField))
        {
            foreach (var aesKey in CandidateKeys(id))
            {
                if (cipher.Length > 32)
                {
                    if (_rsa == null)
                    {
                        continue;
                    }

                    try
                    {
                        var rsa = _rsa!.Value;
                        var decrypted = MegaCrypto.RsaRawDecrypt(cipher, rsa.Exponent, rsa.Modulus);
                        var take = node.IsDirectory ? 16 : 32;
                        if (decrypted.Length < take || !AcceptKey(node, decrypted[..take]))
                        {
                            continue;
                        }

                        return true;
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        continue;
                    }
                }

                var expected = node.IsDirectory ? 16 : 32;
                var aligned = Align(cipher);
                if (aligned.Length != expected)
                {
                    continue;
                }

                var raw = MegaCrypto.AesEcbCrypt(aligned, aesKey, encrypt: false);
                if (AcceptKey(node, raw))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool AcceptKey(RemoteNode node, byte[] raw)
    {
        if (MegaCrypto.IsRejectedKey(raw, node.IsDirectory))
        {
            return false;
        }

        byte[] attributeCipher;
        try
        {
            attributeCipher = MegaCodec.FromBase64(node.AttributeField!);
        }
        catch (FormatException)
        {
            return false;
        }

        var name = MegaCrypto.DecryptName(attributeCipher, MegaCrypto.AttributeKey(raw, node.IsDirectory));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        node.RawKey = raw;
        node.Name = name;
        node.Unlocked = true;
        if (!node.IsDirectory)
        {
            MegaCrypto.GetFileParts(raw, out var aes, out var nonce, out var mac);
            node.AesKey = aes;
            node.Nonce = nonce;
            node.MetaMac = mac;
        }

        return true;
    }

    private IEnumerable<byte[]> CandidateKeys(string? id)
    {
        if (_masterKey != null && (id == null || id == _user || id.Length == 11))
        {
            yield return _masterKey;
        }

        byte[]? share = null;
        if (id != null && _shareKeys.TryGetValue(id, out share))
        {
            yield return share;
        }

        if (id != null && id != _user)
        {
            foreach (var extra in _shareKeys.Values)
            {
                if (share == null || !extra.AsSpan().SequenceEqual(share))
                {
                    yield return extra;
                }
            }
        }
    }

    private static IEnumerable<(string? Id, byte[] Key)> SplitKeys(string field)
    {
        if (field.Length is 22 or 43)
        {
            yield return (null, MegaCodec.FromBase64(field));
            yield break;
        }

        foreach (var part in field.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0 || colon >= part.Length - 1)
            {
                yield return (null, MegaCodec.FromBase64(part));
                continue;
            }

            yield return (part[..colon], MegaCodec.FromBase64(part[(colon + 1)..]));
        }
    }

    private List<RemoteNode> BuildVisible(List<RemoteNode> raw)
    {
        var byHandle = raw.ToDictionary(node => node.Handle, StringComparer.Ordinal);
        var root = raw.FirstOrDefault(node => node.Type == 2);
        if (root == null)
        {
            throw new MegaException("MEGA did not return a Cloud Drive.");
        }

        RootHandle = root.Handle;
        var children = raw.Where(node => node.ParentHandle != null)
            .GroupBy(node => node.ParentHandle!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var visible = new HashSet<string>(StringComparer.Ordinal);

        void Walk(string handle)
        {
            if (!visible.Add(handle) || !children.TryGetValue(handle, out var list))
            {
                return;
            }

            foreach (var child in list)
            {
                if (child.Unlocked)
                {
                    Walk(child.Handle);
                }
            }
        }

        root.Unlocked = true;
        Walk(root.Handle);

        foreach (var node in raw.Where(node => node.Unlocked && node.Type == 1 && !visible.Contains(node.Handle)))
        {
            if (LivesIn(node, byHandle, 3) || LivesIn(node, byHandle, 4))
            {
                continue;
            }

            node.DisplayParent = root.Handle;
            Walk(node.Handle);
        }

        return raw.Where(node => visible.Contains(node.Handle)).ToList();
    }

    private static bool LivesIn(RemoteNode node, Dictionary<string, RemoteNode> byHandle, int type)
    {
        var guard = 0;
        var current = node;
        while (current.ParentHandle != null && byHandle.TryGetValue(current.ParentHandle, out var parent) && guard++ < 64)
        {
            if (parent.Type == type)
            {
                return true;
            }

            if (parent.Type == 2)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private void Remember(RemoteNode node, byte[] rawKey, bool folder)
    {
        node.RawKey = rawKey;
        node.Unlocked = true;
        if (!folder)
        {
            MegaCrypto.GetFileParts(rawKey, out var aes, out var nonce, out var mac);
            node.AesKey = aes;
            node.Nonce = nonce;
            node.MetaMac = mac;
        }

        lock (_gate)
        {
            _nodes[node.Handle] = node;
        }
    }

    private void RemoveTree(string handle)
    {
        if (!_nodes.Remove(handle, out var node))
        {
            return;
        }

        var children = _nodes.Values.Where(item => item.ParentHandle == handle || item.DisplayParent == handle).Select(item => item.Handle).ToArray();
        foreach (var child in children)
        {
            RemoveTree(child);
        }

        TryDelete(Path.Combine(CacheRoot, node.Handle));
    }

    private CloudNode ToCloud(RemoteNode node) => new()
    {
        Handle = node.Handle,
        ParentHandle = node.DisplayParent ?? node.ParentHandle ?? RootHandle ?? "",
        Name = node.Name ?? node.Handle,
        IsDirectory = node.IsDirectory,
        Size = node.Size,
        Modified = node.Modified,
    };

    private RemoteNode ReadCreatedNode(JsonElement response, string parent, string name, bool isDirectory, long size)
    {
        string? handle = null;
        if (response.TryGetProperty("f", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.EnumerateArray())
            {
                if (item.TryGetProperty("h", out var value) && value.GetString() is { } created)
                {
                    handle = created;
                    if (item.TryGetProperty("s", out var reported) && reported.TryGetInt64(out var reportedSize))
                    {
                        size = reportedSize;
                    }

                    break;
                }
            }
        }

        if (handle == null && response.TryGetProperty("e", out var errors))
        {
            var code = NodeError(errors);
            if (code < 0)
            {
                throw MegaException.FromCode(code);
            }
        }

        if (handle == null)
        {
            throw new MegaException("MEGA created the item but did not return its id.");
        }

        return new RemoteNode
        {
            Handle = handle,
            ParentHandle = parent,
            DisplayParent = parent,
            Type = isDirectory ? 1 : 0,
            Name = name,
            Size = size,
            Modified = DateTimeOffset.UtcNow,
            Unlocked = true,
        };
    }

    private static JsonObject PutRequest(string parent, JsonObject node) => new()
    {
        ["a"] = "p",
        ["v"] = 3,
        ["t"] = parent,
        ["n"] = new JsonArray(node),
    };

    // A new folder needs its own node handle. MEGA rejects the put when that field is missing.
    // "xxxxxxxx" is the placeholder the server replaces; the assigned handle comes back in f[].h.
    internal static JsonObject FolderPutRequest(string parent, string attributes, string encryptedKey) => new()
    {
        ["a"] = "p",
        ["t"] = parent,
        ["n"] = new JsonArray(new JsonObject
        {
            ["h"] = "xxxxxxxx",
            ["t"] = 1,
            ["a"] = attributes,
            ["k"] = encryptedKey,
        }),
    };

    private static int NodeError(JsonElement errors)
    {
        if (errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errors.EnumerateArray())
            {
                if (item.TryGetInt32(out var code) && code < 0)
                {
                    return code;
                }
            }
        }
        else if (errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in errors.EnumerateObject())
            {
                if (item.Value.TryGetInt32(out var code) && code < 0)
                {
                    return code;
                }
            }
        }

        return 0;
    }

    private async Task<JsonElement> InvokeAsync(JsonObject command, CancellationToken cancellationToken)
    {
        var payload = "[" + command.ToJsonString() + "]";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var id = Interlocked.Increment(ref _sequence);
            var url = $"https://g.api.mega.co.nz/cs?id={id}";
            if (_sid != null)
            {
                url += "&sid=" + Uri.EscapeDataString(_sid);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            JsonElement body;
            try
            {
                body = JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                throw new MegaException("MEGA returned an unreadable response.");
            }

            if (body.ValueKind == JsonValueKind.Number)
            {
                await HandleNumericAsync(body.GetInt32(), attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() == 0)
            {
                throw new MegaException("MEGA returned an empty response.");
            }

            var first = body[0];
            if (first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out var code) && code < 0)
            {
                await HandleNumericAsync(code, attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return first.Clone();
        }

        throw new MegaException("MEGA kept asking to retry.");
    }

    private static async Task HandleNumericAsync(int code, int attempt, CancellationToken cancellationToken)
    {
        if (code is -3 or -4)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            return;
        }

        throw MegaException.FromCode(code);
    }

    private void EnsureReady()
    {
        if (_sid == null || _masterKey == null)
        {
            throw new MegaException("Sign in to MEGA first.");
        }
    }

    private static string Required(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.GetString() is { } text && text.Length > 0)
        {
            return text;
        }

        throw new MegaException("MEGA returned an incomplete login.");
    }

    private static byte[] Align(byte[] data)
    {
        if (data.Length % 16 == 0)
        {
            return data;
        }

        var padded = new byte[(data.Length + 15) & ~15];
        data.CopyTo(padded, 0);
        return padded;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NimboMount",
        "blocks");

    private sealed class RemoteNode
    {
        public required string Handle { get; init; }

        public string? ParentHandle { get; set; }

        public string? DisplayParent { get; set; }

        public int Type { get; init; }

        public long Size { get; set; }

        public DateTimeOffset Modified { get; set; }

        public string? KeyField { get; init; }

        public string? AttributeField { get; init; }

        public string? ShareKeyField { get; init; }

        public string? Name { get; set; }

        public bool Unlocked { get; set; }

        public byte[]? RawKey { get; set; }

        public byte[]? AesKey { get; set; }

        public byte[]? Nonce { get; set; }

        public byte[]? MetaMac { get; set; }

        public string? DownloadUrl { get; set; }

        public DateTimeOffset DownloadExpires { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsDirectory => Type != 0;
    }

    private readonly record struct BigIntegerPair(System.Numerics.BigInteger Exponent, System.Numerics.BigInteger Modulus);
}
