using System.Text;

namespace NimboMount.Mega;

internal static class MegaCodec
{
    public static byte[] FromBase64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/').Replace(',', '.');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    public static string ToBase64(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static uint[] StringToA32(string value)
    {
        var words = new uint[(value.Length + 3) >> 2];
        for (var i = 0; i < value.Length; i++)
        {
            words[i >> 2] |= (uint)value[i] << (24 - ((i & 3) * 8));
        }

        return words;
    }

    public static byte[] WordsToBytes(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            bytes[i * 4] = (byte)(words[i] >> 24);
            bytes[(i * 4) + 1] = (byte)(words[i] >> 16);
            bytes[(i * 4) + 2] = (byte)(words[i] >> 8);
            bytes[(i * 4) + 3] = (byte)words[i];
        }

        return bytes;
    }

    public static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        return ((uint)data[offset] << 24)
            | ((uint)data[offset + 1] << 16)
            | ((uint)data[offset + 2] << 8)
            | data[offset + 3];
    }

    public static void WriteU32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public static string Utf8(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data);
}
