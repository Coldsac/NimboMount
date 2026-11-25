namespace NimboMount.Drive;

public static class WindowsName
{
    private const char Mark = '\u200B';

    private static readonly Dictionary<char, char> Wide = new()
    {
        ['\\'] = '\uFF3C',
        ['/'] = '\uFF0F',
        [':'] = '\uFF1A',
        ['*'] = '\uFF0A',
        ['?'] = '\uFF1F',
        ['"'] = '\uFF02',
        ['<'] = '\uFF1C',
        ['>'] = '\uFF1E',
        ['|'] = '\uFF5C',
    };

    public static string Encode(string remoteName)
    {
        if (string.IsNullOrEmpty(remoteName))
        {
            return "_";
        }

        var chars = new char[remoteName.Length];
        for (var i = 0; i < remoteName.Length; i++)
        {
            var character = remoteName[i];
            if (character < ' ')
            {
                chars[i] = '_';
            }
            else if (Wide.TryGetValue(character, out var wide))
            {
                chars[i] = wide;
            }
            else
            {
                chars[i] = character;
            }
        }

        var name = new string(chars);
        if (name.Length > 240)
        {
            name = name[..240];
        }

        if (NeedsMark(name))
        {
            if (name.Length >= 240)
            {
                name = name[..239];
            }

            name += Mark;
        }

        return name.Length == 0 ? "_" : name;
    }

    private static bool NeedsMark(string name)
    {
        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return true;
        }

        var stem = name;
        var dot = name.IndexOf('.');
        if (dot == 0)
        {
            return false;
        }

        if (dot > 0)
        {
            stem = name[..dot];
        }

        if (stem.Length is < 3 or > 4)
        {
            return false;
        }

        return stem.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" => true,
            _ => false,
        };
    }
}
