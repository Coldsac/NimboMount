using System.Text;
using System.Text.Json.Nodes;
using NimboMount.Drive;
using NimboMount.Mega;
using Xunit;

namespace NimboMount.Tests;

public class ProtocolTests
{
    [Fact]
    public void AttributesRoundTrip()
    {
        var key = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var cipher = MegaCrypto.EncryptAttributes("quarterly \"report\" / final", key);
        var name = MegaCrypto.DecryptName(cipher, key);
        Assert.Equal("quarterly \"report\" / final", name);
    }

    [Fact]
    public void CounterModeRoundTripsAtAnOffset()
    {
        var key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var nonce = Enumerable.Range(0, 8).Select(value => (byte)(value + 3)).ToArray();
        var plain = Encoding.UTF8.GetBytes("hello mega block that is longer than sixteen bytes");
        var cipher = plain.ToArray();
        MegaCrypto.CryptCtr(cipher, key, nonce, 5);
        Assert.NotEqual(plain, cipher);
        MegaCrypto.CryptCtr(cipher, key, nonce, 5);
        Assert.Equal(plain, cipher);
    }

    [Fact]
    public void FileKeyPacksTheMacAndNonce()
    {
        var aes = Enumerable.Range(0, 16).Select(value => (byte)(value + 1)).ToArray();
        var nonce = Enumerable.Range(0, 8).Select(value => (byte)(20 + value)).ToArray();
        var mac = Enumerable.Range(0, 8).Select(value => (byte)(40 + value)).ToArray();
        var packed = MegaCrypto.PackFileKey(aes, nonce, mac);
        MegaCrypto.GetFileParts(packed, out var aesKey, out var packedNonce, out var packedMac);
        Assert.Equal(aes, aesKey);
        Assert.Equal(nonce, packedNonce);
        Assert.Equal(mac, packedMac);
        Assert.False(MegaCrypto.IsRejectedKey(packed, folder: false));
    }

    [Fact]
    public void ChunkMacAgreesForABufferAndAStream()
    {
        var key = Enumerable.Range(0, 16).Select(value => (byte)(7 + value)).ToArray();
        var nonce = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        var data = new byte[300_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 17);
        }

        MegaCrypto.CryptCtr(data, key, nonce, 0);
        var fromSpan = MegaCrypto.ChunkMac(data, key, nonce);
        using var stream = new MemoryStream(data);
        var fromStream = MegaCrypto.ChunkMac(stream, key, nonce);
        Assert.Equal(8, fromSpan.Length);
        Assert.Equal(fromSpan, fromStream);
    }

    [Fact]
    public void PasswordKeyV2IsStable()
    {
        var salt = new byte[16];
        salt[0] = 4;
        var first = MegaCrypto.DerivePasswordKeyV2("correct horse", salt);
        var second = MegaCrypto.DerivePasswordKeyV2("correct horse", salt);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, MegaCrypto.DerivePasswordKeyV2("other horse", salt));
    }

    [Fact]
    public void PasswordKeyV1IsStable()
    {
        var first = MegaCrypto.DerivePasswordKeyV1("legacy-pass");
        var second = MegaCrypto.DerivePasswordKeyV1("legacy-pass");
        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
        var hash = MegaCrypto.UserHashV1("user@example.com", first);
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.Equal(hash, MegaCrypto.UserHashV1("user@example.com", first));
    }

    [Fact]
    public void FolderCreateRequestCarriesANodeHandle()
    {
        var request = MegaClient.FolderPutRequest("ParentHd", "attr", "key");
        Assert.Equal("p", request["a"]!.GetValue<string>());
        Assert.Equal("ParentHd", request["t"]!.GetValue<string>());
        Assert.False(request.ContainsKey("v"));
        var node = (JsonObject)request["n"]![0]!;
        Assert.Equal("xxxxxxxx", node["h"]!.GetValue<string>());
        Assert.Equal(1, node["t"]!.GetValue<int>());
        Assert.Equal("attr", node["a"]!.GetValue<string>());
        Assert.Equal("key", node["k"]!.GetValue<string>());
    }

    [Fact]
    public void WindowsNamesStayLegal()
    {
        Assert.Equal("notes.txt", WindowsName.Encode("notes.txt"));
        Assert.Equal("report\uFF1Afinal.txt", WindowsName.Encode("report:final.txt"));
        Assert.EndsWith("\u200B", WindowsName.Encode("CON"));
        Assert.EndsWith("\u200B", WindowsName.Encode("CON.txt"));
        Assert.EndsWith("\u200B", WindowsName.Encode("notes "));
        Assert.DoesNotContain(':', WindowsName.Encode("a/b:c*d"));
        Assert.DoesNotContain('/', WindowsName.Encode("a/b:c*d"));
    }
}
