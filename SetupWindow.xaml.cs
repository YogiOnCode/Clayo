using System.Windows;
using System.Windows.Interop;
using CcxShell.Core;
using CcxShell.UI;

namespace CcxShell;

/// <summary>
/// Finds Claude Code and Codex and lets you choose which Clayo uses (design/setup,
/// docs/SETUP.md). It saves nothing itself: Open Clayo hands the choices to MainWindow, which
/// owns the settings, so a later save there can't undo them. Start at login is the one
/// exception, written at once as in Settings, because it lives in the Run key.
/// </summary>
public partial class SetupWindow : Window
{
    /// <summary>Open Clayo was pressed. MainWindow saves these and shows itself.</summary>
    public event Action<ClayoSettings>? Done;

    private readonly ClayoSettings _settings;
    private AgentInfo[]? _found;
    private MascotMove? _move;

    // Check again can be pressed before the last check is back; only the newest one counts.
    private int _run;

    public SetupWindow(ClayoSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ClaudeRow.Use = settings.UseClaude;
        CodexRow.Use = settings.UseCodex;
        ClaudeRow.UseChanged += ShowFound;
        CodexRow.UseChanged += ShowFound;
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
        var move = view.Claude.State == AgentRowState.Looking ? MascotMove.Peek
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
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e) => Detect();

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
