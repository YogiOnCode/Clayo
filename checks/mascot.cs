#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CcxShell.UI;

// A visual check, not a pass/fail one: renders the WPF mascot and the HTML design to PNGs
// to compare by eye. Usage: dotnet run --file checks/mascot.cs [out-dir]
var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "clayo-mascot");
Directory.CreateDirectory(outDir);

// WPF needs an STA thread. The control is never shown, so it holds its static pose: exactly
// the frame to compare, with no animation clock in the way.
var ui = new Thread(() =>
{
    Render(MascotMove.Idle, "wpf-idle.png", 256);
    Render(MascotMove.Peek, "wpf-peek.png", 256);
    // The real island size, where the 1-unit details are under a pixel.
    Render(MascotMove.Idle, "wpf-idle-44.png", 44);
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();

void Render(MascotMove move, string name, int size)
{
    var mascot = new ClayoMascot();
    mascot.Play(move);
    // The design's desk colour, so the dark extrusion reads as it does there.
    var root = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0f, 0x11, 0x14)), Child = mascot };
    root.Measure(new Size(size, size));
    root.Arrange(new Rect(0, 0, size, size));
    root.UpdateLayout();

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(root);
    var png = new PngBitmapEncoder();
    png.Frames.Add(BitmapFrame.Create(bmp));
    var path = Path.Combine(outDir, name);
    using (var f = File.Create(path)) png.Save(f);
    Console.WriteLine($"wrote {path}");
}

// The design page starts on the clay look; copies switched to voxel (and to peek, through
// the page script, which owns the classes), so design/ stays as is. The virtual time budget
// lets the glasses finish their transition before the shot.
// Run from the repo root, like the other checks.
var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
var design = Path.GetFullPath(Path.Combine("design", "mascot", "clayo-mascot.html"));
if (!File.Exists(edge) || !File.Exists(design))
{
    Console.WriteLine("SKIP  html reference (Edge or the design file not found)");
    return 0;
}
var html = File.ReadAllText(design).Replace("data-look=\"clay\"", "data-look=\"voxel\"");
int fail = 0;
foreach (var (name, source) in new[] {
    ("html-idle", html),
    ("html-peek", html.Replace("let moves = [];", "let moves = ['peek'];")) })
{
    var page = Path.Combine(outDir, name + ".html");
    var shot = Path.Combine(outDir, name + ".png");
    File.WriteAllText(page, source);
    File.Delete(shot);   // a stale one would pass for a failed run
    using (var p = Process.Start(edge, ["--headless=new", "--disable-gpu", "--hide-scrollbars",
                                        "--window-size=860,520", "--virtual-time-budget=600",
                                        $"--screenshot={shot}",
                                        new Uri(page).AbsoluteUri]))
        p.WaitForExit(30_000);
    if (File.Exists(shot)) Console.WriteLine($"wrote {shot}");
    else { fail++; Console.WriteLine($"FAIL  {shot} was not written"); }
}
return fail == 0 ? 0 : 1;
