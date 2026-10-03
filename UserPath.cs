using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CcxShell.Core;

/// <summary>
/// Clayo's folder on the user's PATH (HKCU\Environment), so `clayo` typed in Explorer's
/// address bar or a shell finds it. Edited in the registry rather than with setx, which cuts
/// PATH at 1024 characters, and every other entry is kept as written, %VARS% unexpanded.
/// </summary>
public sealed class UserPath(string keyPath)
{
    private const string EnvironmentKey = "Environment";

    public static UserPath ForThisUser() => new(EnvironmentKey);

    /// <summary>The folder clayo.exe runs from.</summary>
    public static string? ClayoFolder => Path.GetDirectoryName(Environment.ProcessPath);

    /// <summary>
    /// As LoginStartup: a dev build never writes the real PATH, or `clayo` would start
    /// bin\Debug. Any other key is fair game, so checks/userpath.cs can run in Debug.
    /// </summary>
    public bool CanWrite =>
#if DEBUG
        keyPath != EnvironmentKey;
#else
        true;
#endif

    public bool Contains(string folder) => Entries(Read().value).Any(e => Same(e, folder));

    public void Set(bool on, string folder)
    {
        if (!CanWrite) return;
        var (value, kind) = Read();
        var entries = Entries(value).ToList();
        bool has = entries.Any(e => Same(e, folder));
        if (on == has) return;

        if (on) entries.Add(folder);
        else entries.RemoveAll(e => Same(e, folder));

        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("Path", string.Join(';', entries), kind);
        if (keyPath == EnvironmentKey) Broadcast();
    }

    /// <summary>The raw value, %VARS% and all, and its type: REG_EXPAND_SZ unless it was plain.</summary>
    private (string value, RegistryValueKind kind) Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            return ("", RegistryValueKind.ExpandString);
        return (value, key.GetValueKind("Path") == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
    }

    private static IEnumerable<string> Entries(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(e => e.Trim().Length > 0);

    /// <summary>One folder however it's written: quoted, with a trailing slash, or through a %VAR%.</summary>
    private static bool Same(string entry, string folder) =>
        string.Equals(Norm(Environment.ExpandEnvironmentVariables(entry)), Norm(folder), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string path) => path.Trim().Trim('"').TrimEnd('\\', '/');

    /// <summary>Tells Explorer and new shells that PATH changed, so `clayo` works without signing out.</summary>
    private static void Broadcast() =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
                                                    uint flags, uint timeout, out IntPtr result);
}
