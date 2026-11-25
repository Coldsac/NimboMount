using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NimboMount.Mega;

internal static class MegaCrypto
{
    public static byte[] DerivePasswordKeyV2(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA512,
            32);
    }

    public static byte[] DerivePasswordKeyV1(string password)
    {
        Span<uint> state = stackalloc uint[4];
        state[0] = 0x93C467E3;
        state[1] = 0x7DB0C7A4;
        state[2] = 0xD1BE3F81;
        state[3] = 0x0152CB56;

        var words = MegaCodec.StringToA32(password);
        Span<byte> block = stackalloc byte[16];
        Span<byte> key = stackalloc byte[16];
        for (var round = 0; round < 65536; round++)
        {
            for (var j = 0; j < words.Length; j += 4)
            {
                for (var i = 0; i < 4; i++)
                {
                    var word = i + j < words.Length ? words[i + j] : 0;
                    WriteWord(key, i, word);
                }

                WriteWord(block, 0, state[0]);
                WriteWord(block, 1, state[1]);
                WriteWord(block, 2, state[2]);
                WriteWord(block, 3, state[3]);
                AesEcb(block, key, encrypt: true);
                state[0] = MegaCodec.ReadU32(block, 0);
                state[1] = MegaCodec.ReadU32(block, 4);
                state[2] = MegaCodec.ReadU32(block, 8);
                state[3] = MegaCodec.ReadU32(block, 12);
            }
        }

        return MegaCodec.WordsToBytes(new[] { state[0], state[1], state[2], state[3] });
    }

    public static string UserHashV1(string email, byte[] passwordKey)
    {
        var words = MegaCodec.StringToA32(email);
        Span<uint> hash = stackalloc uint[4];
        for (var i = 0; i < words.Length; i++)
        {
            hash[i % 4] ^= words[i];
        }

        Span<byte> block = stackalloc byte[16];
        for (var round = 0; round < 16384; round++)
        {
            WriteWord(block, 0, hash[0]);
            WriteWord(block, 1, hash[1]);
            WriteWord(block, 2, hash[2]);
            WriteWord(block, 3, hash[3]);
            AesEcb(block, passwordKey, encrypt: true);
            hash[0] = MegaCodec.ReadU32(block, 0);
            hash[1] = MegaCodec.ReadU32(block, 4);
            hash[2] = MegaCodec.ReadU32(block, 8);
            hash[3] = MegaCodec.ReadU32(block, 12);
        }

        return MegaCodec.ToBase64(MegaCodec.WordsToBytes(new[] { hash[0], hash[2] }));
    }

    public static byte[] AesEcbCrypt(byte[] data, byte[] key, bool encrypt)
    {
        if (data.Length == 0 || data.Length % 16 != 0)
        {
            throw new CryptographicException("MEGA block data must be a multiple of 16 bytes.");
        }

        using var aes = CreateAes();
        aes.Key = key;
        aes.IV = new byte[16];
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    public static void AesEcb(Span<byte> block, ReadOnlySpan<byte> key, bool encrypt)
    {
        var data = block.ToArray();
        var keyBytes = key.ToArray();
        var output = AesEcbCrypt(data, keyBytes, encrypt);
        output.CopyTo(block);
    }

    public static byte[] EncryptAttributes(string name, byte[] attributeKey)
    {
        var json = "MEGA{\"n\":" + JsonSerializer.Serialize(name) + "}";
        var raw = Encoding.UTF8.GetBytes(json);
        var padded = new byte[(raw.Length + 15) & ~15];
        raw.CopyTo(padded, 0);
        return AesCbc(padded, attributeKey, encrypt: true);
    }

    public static string? DecryptName(byte[] attributes, byte[] attributeKey)
    {
        if (attributes.Length == 0 || attributes.Length % 16 != 0)
        {
            return null;
        }

        byte[] plain;
        try
        {
            plain = AesCbc(attributes, attributeKey, encrypt: false);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var end = Array.IndexOf(plain, (byte)0);
        if (end < 0)
        {
            end = plain.Length;
        }

        var text = Encoding.UTF8.GetString(plain, 0, end);
        if (!text.StartsWith("MEGA{", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[4..]);
            if (doc.RootElement.TryGetProperty("n", out var name) && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static byte[] AttributeKey(byte[] nodeKey, bool folder)
    {
        if (folder)
        {
            if (nodeKey.Length < 16)
            {
                throw new CryptographicException("Folder key is too short.");
            }

            return nodeKey.AsSpan(0, 16).ToArray();
        }

        if (nodeKey.Length < 32)
        {
            throw new CryptographicException("File key is too short.");
        }

        var key = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)(nodeKey[i] ^ nodeKey[i + 16]);
        }

        return key;
    }

    public static bool IsRejectedKey(byte[] nodeKey, bool folder)
    {
        if (folder)
        {
            return nodeKey.Length >= 16 && nodeKey.AsSpan(0, 16).SequenceEqual(new byte[16]);
        }

        if (nodeKey.Length < 32)
        {
            return true;
        }

        return nodeKey.AsSpan(0, 16).SequenceEqual(nodeKey.AsSpan(16, 16));
    }

    public static void GetFileParts(byte[] nodeKey, out byte[] aesKey, out byte[] nonce, out byte[] metaMac)
    {
        aesKey = AttributeKey(nodeKey, folder: false);
        nonce = nodeKey.AsSpan(16, 8).ToArray();
        metaMac = nodeKey.AsSpan(24, 8).ToArray();
    }

    public static byte[] PackFileKey(byte[] aesKey, byte[] nonce, byte[] metaMac)
    {
        var key = new byte[32];
        nonce.AsSpan(0, 8).CopyTo(key.AsSpan(16));
        metaMac.AsSpan(0, 8).CopyTo(key.AsSpan(24));
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)(aesKey[i] ^ key[i + 16]);
        }

        return key;
    }

    public static void CryptCtr(Span<byte> data, byte[] aesKey, byte[] nonce, long offset)
    {
        using var aes = CreateAes();
        aes.Key = aesKey;
        aes.IV = new byte[16];
        using var encryptor = aes.CreateEncryptor();
        Span<byte> counter = stackalloc byte[16];
        Span<byte> stream = stackalloc byte[16];
        var counterBytes = new byte[16];
        var streamBytes = new byte[16];
        nonce.AsSpan(0, 8).CopyTo(counter);
        var index = offset / 16;
        var skip = (int)(offset % 16);
        var position = 0;
        while (position < data.Length)
        {
            WriteU64(counter[8..], (ulong)index);
            counter.CopyTo(counterBytes);
            encryptor.TransformBlock(counterBytes, 0, 16, streamBytes, 0);
            streamBytes.CopyTo(stream);
            var available = 16 - skip;
            var count = Math.Min(available, data.Length - position);
            for (var i = 0; i < count; i++)
            {
                data[position + i] ^= stream[skip + i];
            }

            position += count;
            skip = 0;
            index++;
        }
    }

    public static byte[] ChunkMac(ReadOnlySpan<byte> ciphertext, byte[] aesKey, byte[] nonce)
    {
        var fileMac = new byte[16];
        var iv = new byte[16];
        nonce.AsSpan(0, 8).CopyTo(iv);
        long position = 0;
        var chunk = 0x20000;
        while (position < ciphertext.Length)
        {
            var length = (int)Math.Min(chunk, ciphertext.Length - position);
            MacChunk(ciphertext.Slice((int)position, length), aesKey, iv, fileMac);
            position += length;
            if (chunk < 0x100000)
            {
                chunk += 0x20000;
            }
        }

        return CondenseMac(fileMac);
    }

    public static byte[] ChunkMac(Stream ciphertext, byte[] aesKey, byte[] nonce)
    {
        var fileMac = new byte[16];
        var iv = new byte[16];
        nonce.AsSpan(0, 8).CopyTo(iv);
        var chunk = 0x20000;
        var buffer = new byte[0x100000];
        while (true)
        {
            var filled = 0;
            while (filled < chunk)
            {
                var read = ciphertext.Read(buffer, filled, chunk - filled);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled == 0)
            {
                break;
            }

            MacChunk(buffer.AsSpan(0, filled), aesKey, iv, fileMac);
            if (filled < chunk)
            {
                break;
            }

            if (chunk < 0x100000)
            {
                chunk += 0x20000;
            }
        }

        return CondenseMac(fileMac);
    }

    public static IReadOnlyList<BigInteger> ReadMpis(byte[] data, int count)
    {
        var values = new List<BigInteger>(count);
        var position = 0;
        for (var i = 0; i < count; i++)
        {
            if (position + 2 > data.Length)
            {
                break;
            }

            var bits = (data[position] << 8) + data[position + 1];
            var length = (bits + 7) >> 3;
            position += 2;
            if (length < 0 || position + length > data.Length)
            {
                break;
            }

            var mpi = data.AsSpan(position, length).ToArray();
            values.Add(mpi.Length == 0 ? BigInteger.Zero : new BigInteger(mpi, isUnsigned: true, isBigEndian: true));
            position += length;
        }

        return values;
    }

    public static byte[] RsaRawDecrypt(byte[] mpiCipher, BigInteger exponent, BigInteger modulus)
    {
        if (mpiCipher.Length < 2)
        {
            throw new CryptographicException("Session challenge is too short.");
        }

        var bits = (mpiCipher[0] << 8) + mpiCipher[1];
        var length = (bits + 7) >> 3;
        if (length <= 0 || mpiCipher.Length < 2 + length)
        {
            throw new CryptographicException("Session challenge length is invalid.");
        }

        var cipher = mpiCipher.AsSpan(2, length).ToArray();
        var value = new BigInteger(cipher, isUnsigned: true, isBigEndian: true);
        var plain = BigInteger.ModPow(value, exponent, modulus);
        var modulusLength = (int)((modulus.GetBitLength() + 7) / 8);
        var bytes = plain.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length < modulusLength)
        {
            var padded = new byte[modulusLength];
            bytes.CopyTo(padded.AsSpan(modulusLength - bytes.Length));
            bytes = padded;
        }

        if (bytes.Length < 2 || bytes[1] != 0)
        {
            var adjusted = new byte[bytes.Length + 1];
            bytes.CopyTo(adjusted.AsSpan(1));
            bytes = adjusted;
        }

        return bytes.Length <= 2 ? Array.Empty<byte>() : bytes[2..];
    }

    public static BigInteger ModInverse(BigInteger value, BigInteger modulus)
    {
        var t = BigInteger.Zero;
        var newT = BigInteger.One;
        var r = modulus;
        var newR = value % modulus;
        if (newR < 0)
        {
            newR += modulus;
        }

        while (newR != 0)
        {
            var quotient = r / newR;
            (t, newT) = (newT, t - (quotient * newT));
            (r, newR) = (newR, r - (quotient * newR));
        }

        if (r > 1)
        {
            throw new CryptographicException("RSA inverse does not exist.");
        }

        if (t < 0)
        {
            t += modulus;
        }

        return t;
    }

    public static byte[] Random(int length)
    {
        var data = new byte[length];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static byte[] AesCbc(byte[] data, byte[] key, bool encrypt, byte[]? iv = null)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv ?? new byte[16];
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    private static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    private static void WriteWord(Span<byte> target, int index, uint word)
    {
        MegaCodec.WriteU32(target, index * 4, word);
    }

    private static void WriteU64(Span<byte> target, ulong value)
    {
        target[0] = (byte)(value >> 56);
        target[1] = (byte)(value >> 48);
        target[2] = (byte)(value >> 40);
        target[3] = (byte)(value >> 32);
        target[4] = (byte)(value >> 24);
        target[5] = (byte)(value >> 16);
        target[6] = (byte)(value >> 8);
        target[7] = (byte)value;
    }

    private static void MacChunk(ReadOnlySpan<byte> chunk, byte[] aesKey, byte[] iv, byte[] fileMac)
    {
        var paddedLength = (chunk.Length + 15) & ~15;
        var padded = new byte[paddedLength];
        chunk.CopyTo(padded);
        var encrypted = AesCbc(padded, aesKey, encrypt: true, iv);
        Xor(fileMac, encrypted.AsSpan(encrypted.Length - 16, 16));
    }

    private static byte[] CondenseMac(byte[] fileMac)
    {
        var condensed = new byte[8];
        MegaCodec.WriteU32(condensed, 0, MegaCodec.ReadU32(fileMac, 0) ^ MegaCodec.ReadU32(fileMac, 4));
        MegaCodec.WriteU32(condensed, 4, MegaCodec.ReadU32(fileMac, 8) ^ MegaCodec.ReadU32(fileMac, 12));
        return condensed;
    }

    private static void Xor(Span<byte> left, ReadOnlySpan<byte> right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            left[i] ^= right[i];
        }
    }
}
