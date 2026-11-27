namespace NimboMount.Drive;

internal static class FileRetry
{
    internal static TimeSpan SharingBudget { get; set; } = TimeSpan.FromSeconds(12);

    internal static void Copy(string source, string destination)
    {
        Run(() => File.Copy(source, destination, overwrite: false));
    }

    internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        return Run(() => new FileStream(path, mode, access, share, 1024 * 1024, options));
    }

    internal static void Run(Action action)
    {
        Run<object?>(() =>
        {
            action();
            return null;
        });
    }

    internal static T Run<T>(Func<T> action)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var deadline = DateTime.UtcNow + SharingBudget;
        while (true)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow + delay < deadline)
            {
                Thread.Sleep(delay);
                if (delay < TimeSpan.FromSeconds(2))
                {
                    delay += delay;
                }
            }
        }
    }

    internal static bool IsSharingViolation(Exception ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
