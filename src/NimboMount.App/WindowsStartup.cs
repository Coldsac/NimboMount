using Microsoft.Win32;

namespace NimboMount;

internal static class WindowsStartup
{
    private const string ValueName = "NimboMount";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows did not open the startup list.");
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);

        if (!enabled)
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("NimboMount could not find its own program path.");
        }

        run.SetValue(ValueName, "\"" + path + "\" --startup");
    }
}
