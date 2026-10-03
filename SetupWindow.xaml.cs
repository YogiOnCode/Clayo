using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using CcxShell.Core;
using CcxShell.UI;

namespace CcxShell;

/// <summary>
/// Finds Claude Code and Codex and lets you choose which Clayo uses (design/setup,
/// docs/SETUP.md). It saves nothing itself: Open Clayo hands the choices to MainWindow, which
/// owns the settings, so a later save there can't undo them. Start at login is the one
/// exception, written at once as in Settings, because it lives in the Run key. PATH is
/// written on Open Clayo, because ticked is its default before anything was written.
/// </summary>
public partial class SetupWindow : Window
{
    /// <summary>Open Clayo was pressed. MainWindow saves these and shows itself.</summary>
    public event Action<ClayoSettings>? Done;

    // Locate… changes the paths here; they are saved with the rest on Open Clayo.
    private ClayoSettings _settings;
    private AgentInfo[]? _found;
    private MascotMove? _move;

    // Check again can be pressed before the last check is back; only the newest one counts.
    private int _run;

    // Install or Sign in has its window open. One at a time, and Check again waits for it.
    private bool _busy;

    public SetupWindow(ClayoSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ClaudeRow.Use = settings.UseClaude;
        CodexRow.Use = settings.UseCodex;
        ClaudeRow.UseChanged += ShowFound;
        CodexRow.UseChanged += ShowFound;
        foreach (var row in new[] { ClaudeRow, CodexRow })
        {
            row.InstallClicked += kind => RunHelper(kind, install: true);
            row.SignInClicked += kind => RunHelper(kind, install: false);
            row.LocateClicked += Locate;
        }
        (settings.DefaultAgent switch
        {
            NewSessionAgent.Codex => PickCodex,
            NewSessionAgent.Ask => PickAsk,
            _ => PickClaude
        }).IsChecked = true;

        // As in Settings: a dev build shows what the installed Clayo set but never writes it.
        var startup = LoginStartup.ForThisUser();
        LoginTick.IsChecked = startup.IsOn;
        LoginTick.IsEnabled = startup.CanWrite;
        if (!startup.CanWrite) LoginText.Text += " Only the installed Clayo can change this.";

        // Ticked until setup was done once; after that, whether the folder is on PATH now.
        var path = UserPath.ForThisUser();
        PathTick.IsChecked = settings.SetupDone || !path.CanWrite
            ? UserPath.ClayoFolder is { } folder && path.Contains(folder)
            : true;
        PathTick.IsEnabled = path.CanWrite;
        if (!path.CanWrite) PathText.Inlines.Add(" Only the installed Clayo can change this.");

        Loaded += (_, _) => Detect();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MainWindow.DarkCaption(new WindowInteropHelper(this).Handle);
    }

    private async void Detect()
    {
        int run = ++_run;
        _found = null;
        Show(SetupScreen.Checking(), null, null);
        var found = await AgentDetector.ForThisMachine().DetectAsync(_settings.ClaudePath, _settings.CodexPath);
        if (run != _run) return;
        _found = found;
        ShowFound();
    }

    private void ShowFound()
    {
        if (_found is not [var claude, var codex]) return;
        Show(SetupScreen.From(claude, codex, ClaudeRow.Use, CodexRow.Use), claude, codex);
    }

    private void Show(SetupView view, AgentInfo? claude, AgentInfo? codex)
    {
        // Peeking while it looks, a jump (twice, then still) once Clayo can open, else at ease.
        // Only on a change, so ticking Use doesn't make it jump again.
        // Walking, as on the island, while an install or a sign-in runs.
        bool waiting = view.Claude.State is AgentRowState.Installing or AgentRowState.SigningIn
                    || view.Codex.State is AgentRowState.Installing or AgentRowState.SigningIn;
        var move = view.Claude.State == AgentRowState.Looking ? MascotMove.Peek
                 : waiting ? MascotMove.Walk
                 : view.CanOpen ? MascotMove.Jump
                 : MascotMove.Idle;
        if (move != _move)
        {
            _move = move;
            Mascot.Play(move);
        }
        Headline.Text = view.Title;
        Intro.Text = view.Intro;
        Note.Text = view.Note;
        ClaudeRow.Show(view.Claude, claude);
        CodexRow.Show(view.Codex, codex);
        Picker.Visibility = view.ShowPicker ? Visibility.Visible : Visibility.Collapsed;
        Options.IsEnabled = view.OptionsEnabled;
        Open.IsEnabled = view.CanOpen;
        CheckAgain.IsEnabled = !_busy;
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => Detect();

    /// <summary>
    /// Install or Sign in: the agent's own command in a PowerShell window (AgentHelper), then a
    /// fresh check once that window is closed, however it went.
    /// </summary>
    private async void RunHelper(AgentKind kind, bool install)
    {
        if (_busy || _found is not [var claude, var codex]) return;
        var agent = kind == AgentKind.Claude ? claude : codex;
        if ((install ? AgentHelper.InstallCommand(agent) : AgentHelper.SignInCommand(agent)) is not { } command) return;

        _busy = true;
        var found = SetupScreen.From(claude, codex, ClaudeRow.Use, CodexRow.Use);
        Show(install ? SetupScreen.Installing(found, kind) : SetupScreen.SigningIn(found, kind), claude, codex);
        try
        {
            await AgentHelper.RunAsync(command);
        }
        catch (Win32Exception)
        {
            // No powershell.exe to start: the check below shows the row as it was.
        }
        _busy = false;
        Activate();
        Detect();
    }

    /// <summary>
    /// Locate…: a program that answers --version becomes that agent's saved path, and is
    /// checked like any other.
    /// </summary>
    private async void Locate(AgentKind kind)
    {
        var name = SetupScreen.Name(kind);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Where is {name}?",
            Filter = "Programs (*.exe;*.cmd)|*.exe;*.cmd"
        };
        if (dialog.ShowDialog(this) != true) return;

        var file = dialog.FileName;
        if (await Task.Run(() => AgentDetector.Run(file, "--version")) is null)
        {
            Note.Text = $"That program didn't run. Pick {name}'s {(kind == AgentKind.Claude ? "claude.exe" : "codex.cmd")}.";
            return;
        }
        _settings = kind == AgentKind.Claude ? _settings with { ClaudePath = file } : _settings with { CodexPath = file };
        Detect();
    }

    private void LoginTick_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe) LoginStartup.ForThisUser().Set(LoginTick.IsChecked == true, exe);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        // With only one agent to use there is nothing to pick: New session starts that one.
        var agent = Picker.IsVisible
            ? PickCodex.IsChecked == true ? NewSessionAgent.Codex
            : PickAsk.IsChecked == true ? NewSessionAgent.Ask
            : NewSessionAgent.Claude
            : _found is [var claude, _] && SetupScreen.StateOf(claude) == AgentRowState.Ready && ClaudeRow.Use
                ? NewSessionAgent.Claude
                : NewSessionAgent.Codex;

        var path = UserPath.ForThisUser();
        if (path.CanWrite && UserPath.ClayoFolder is { } folder) path.Set(PathTick.IsChecked == true, folder);

        Done?.Invoke(_settings with
        {
            SetupDone = true,
            UseClaude = ClaudeRow.Use,
            UseCodex = CodexRow.Use,
            DefaultAgent = agent
        });
        Close();
    }
}
