namespace NimboMount.Mega;

public sealed class MegaException : Exception
{
    public MegaException(string message, int code = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public int Code { get; }

    public bool MfaRequired => Code == -26;

    public bool SessionExpired => Code == -15;

    public static MegaException FromCode(int code) => code switch
    {
        -1 => new MegaException("MEGA had an internal error.", code),
        -2 => new MegaException("MEGA rejected the request.", code),
        -3 => new MegaException("MEGA asked to retry.", code),
        -4 => new MegaException("MEGA rate limit. Wait a moment and try again.", code),
        -9 => new MegaException("MEGA could not find that account or file.", code),
        -11 => new MegaException("MEGA denied access.", code),
        -12 => new MegaException("An item with that name already exists.", code),
        -14 => new MegaException("A decryption key was rejected.", code),
        -15 => new MegaException("The saved MEGA session expired. Sign in again.", code),
        -16 => new MegaException("This MEGA account is blocked.", code),
        -17 => new MegaException("This MEGA account is over its storage quota.", code),
        -18 => new MegaException("MEGA is temporarily unavailable.", code),
        -26 => new MegaException("MEGA needs the six-digit code from your authenticator.", code),
        _ => new MegaException($"MEGA returned error {code}.", code),
    };
}
