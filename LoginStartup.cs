using System.IO;
using Microsoft.Win32;

namespace CcxShell.Core;

/// <summary>
/// Clayo starts with Windows from the per-user Run key, with its window hidden, so the island
/// is there and sessions can be picked up from anywhere. On by default; turning it off in the
/// island's menu leaves a marker, because otherwise the next start would just write it back.
/// </summary>
public sealed class LoginStartup(string keyPath, string valueName, string optOutPath)
{
    public const string BackgroundArg = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static LoginStartup ForThisUser() => new(RunKey, "Clayo", Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "no-login-start"));

    /// <summary>
    /// A dev build never writes the real Run key: it would point login at bin\Debug instead of
    /// the installed Clayo. Any other key is fair game, so checks/startup.cs can run in Debug.
    /// </summary>
    public bool CanWrite =>
#if DEBUG
        keyPath != RunKey;
#else
        true;
#endif

    public bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is not null;
        }
    }

    /// <summary>
    /// At every start: unless turned off, make sure login launches this exe. Rewriting it each
    /// time means a Clayo that moved (a new publish folder) is the one that starts.
    /// </summary>
    public void Keep(string exePath)
    {
        if (CanWrite && !File.Exists(optOutPath)) Write(exePath);
    }

    public void Set(bool on, string exePath)
    {
        if (!CanWrite) return;
        if (on)
        {
            File.Delete(optOutPath);
            Write(exePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(optOutPath)!);
        File.WriteAllText(optOutPath, "");
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private void Write(string exePath)
    {
        var command = $"\"{exePath}\" {BackgroundArg}";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key.GetValue(valueName) as string != command) key.SetValue(valueName, command);
    }
}
