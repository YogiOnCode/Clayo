using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using CcxShell.Core;

namespace CcxShell;

/// <summary>One agent's row in the setup window. SetupWindow decides what it says (SetupScreen).</summary>
public partial class AgentRow : UserControl
{
    /// <summary>The Use tick changed. SetupWindow works out whether Clayo can open now.</summary>
    public event Action? UseChanged;

    /// <summary>Install, Sign in or Locate… was pressed on this row. SetupWindow does the work.</summary>
    public event Action<AgentKind>? InstallClicked, SignInClicked, LocateClicked;

    public AgentKind Kind { get; private set; }

    private static readonly Brush Muted = Freeze("#8A929F");

    public AgentRow() => InitializeComponent();

    public bool Use
    {
        get => UseTick.IsChecked == true;
        set => UseTick.IsChecked = value;
    }

    public void Show(AgentRowView row, AgentInfo? found)
    {
        Kind = row.Kind;
        bool claude = row.Kind == AgentKind.Claude;
        AgentName.Text = SetupScreen.Name(row.Kind);
        Meta.Text = row.Meta;
        BadgeText.Text = claude ? "CC" : "CX";
        AutomationProperties.SetName(UseTick, $"Use {SetupScreen.Name(row.Kind)}");

        // The badge takes its agent's colours only once the agent is there.
        bool lit = row.State is AgentRowState.Ready or AgentRowState.SignIn or AgentRowState.SigningIn;
        Badge.Background = Freeze(!lit ? "#1B1F26" : claude ? "#2A221F" : "#1C2530");
        Badge.BorderBrush = Freeze(!lit ? "#2F3641" : claude ? "#4A3329" : "#2B3D52");
        BadgeText.Foreground = !lit ? Muted : Freeze(claude ? "#E39A7E" : "#7DBBEB");

        var (text, color) = row.State switch
        {
            AgentRowState.Looking => ("Looking…", "#D4A24C"),
            AgentRowState.Ready => ("Ready", "#6BBF8A"),
            AgentRowState.SignIn => ("Not signed in", "#D4A24C"),
            AgentRowState.Installing => ("Installing…", "#D4A24C"),
            AgentRowState.SigningIn => ("Signing in…", "#D4A24C"),
            _ => ("Not installed", "#8A929F"),
        };
        PillText.Text = text;
        PillText.Foreground = PillDot.Fill = Freeze(color);
        // 12% of the pill's own colour, as the design's rgba(…, 0.12).
        Pill.Background = Freeze("#1F" + color[1..]);

        UseTick.Visibility = Shown(row.State == AgentRowState.Ready);
        SignIn.Visibility = Shown(row.State == AgentRowState.SignIn);
        Fix.Visibility = Shown(row.State == AgentRowState.Missing);

        InstallCommand.Text = claude ? "irm https://claude.ai/install.ps1 | iex" : "npm install -g @openai/codex";
        bool needsNode = !claude && found is { Npm: null };
        Install.Visibility = Shown(!needsNode);
        GetNode.Visibility = Shown(needsNode);
    }

    private void UseTick_Click(object sender, RoutedEventArgs e) => UseChanged?.Invoke();

    private void Install_Click(object sender, RoutedEventArgs e) => InstallClicked?.Invoke(Kind);

    private void SignIn_Click(object sender, RoutedEventArgs e) => SignInClicked?.Invoke(Kind);

    private void Locate_Click(object sender, RoutedEventArgs e) => LocateClicked?.Invoke(Kind);

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }

    private static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
