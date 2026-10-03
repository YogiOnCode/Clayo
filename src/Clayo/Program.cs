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
        return RunApp();
    }

    // Kept out of Main so compiling Main for the relay does not load WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
