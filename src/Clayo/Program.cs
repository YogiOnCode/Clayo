using System.IO;
using System.Runtime.CompilerServices;
using CcxShell.Core;

namespace CcxShell;

/// <summary>
/// Our own entry point instead of the one WPF generates, so `clayo --statusline` can answer
/// before any WPF assembly loads. Claude Code runs it on every status refresh.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [StatusRelay.Arg, ..]) return StatusRelay.Run(pass: args is [_, StatusRelay.PassArg, ..]);
        if (args is [PageCheck.Arg, ..]) return CheckPage(args[1..]);
        // clayo-page.exe is this program again, as a console one (see the csproj).
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == PageCheck.Tool) return CheckPage(args);
        return RunApp();
    }

    // Out of Main for the same reason: the check loads WebView2 and a dispatcher.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CheckPage(string[] args) => PageCheck.Run(args);

    // Kept out of Main so compiling Main for the relay does not load WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
