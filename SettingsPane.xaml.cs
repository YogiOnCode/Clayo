using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcxShell.Core;

namespace CcxShell;

/// <summary>
/// Clayo's settings, in the terminal side's place (design/settings/settings-prototype.html).
/// MainWindow shows and hides it; the sessions keep running behind it.
/// </summary>
public partial class SettingsPane : UserControl
{
    /// <summary>The × was pressed. MainWindow goes back to the session you were on.</summary>
    public event Action? CloseRequested;

    /// <summary>A status bar option changed. MainWindow saves it and redraws the header.</summary>
    public event Action<ClayoSettings>? SettingsChanged;

    private ClayoSettings _settings = new();

    public SettingsPane() => InitializeComponent();

    /// <summary>
    /// Brings the pages up to date each time Settings opens: the login entry can be changed
    /// from the gear and island menus, or removed outside Clayo, while it was closed.
    /// </summary>
    public void Refresh(ClayoSettings settings)
    {
        _settings = settings;
        HeaderSwitch.IsChecked = settings.StatusHeader;
        FooterSwitch.IsChecked = settings.StatusFooter;
        ModelTick.IsChecked = settings.StatusModel;
        BranchTick.IsChecked = settings.StatusBranch;
        ContextTick.IsChecked = settings.StatusContext;
        EffortTick.IsChecked = settings.StatusEffort;
        FiveHourTick.IsChecked = settings.StatusFiveHour;
        SevenDayTick.IsChecked = settings.StatusSevenDay;
        ShowStatusPage();

        var startup = LoginStartup.ForThisUser();
        LoginSwitch.IsChecked = startup.IsOn;
        LoginSwitch.IsEnabled = startup.CanWrite;
        LoginRow.Cursor = startup.CanWrite ? Cursors.Hand : Cursors.Arrow;
        // A dev build shows what the installed Clayo set but never writes the Run key.
        LoginText.Text = startup.CanWrite
            ? "Clayo starts hidden; the island greets you."
            : "Clayo starts hidden; the island greets you. Only the installed Clayo can change this.";
        ShowLoginState();
    }

    private void ShowLoginState() => LoginState.Text = LoginSwitch.IsChecked == true ? "On" : "Off";

    private void LoginSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe) LoginStartup.ForThisUser().Set(LoginSwitch.IsChecked == true, exe);
        ShowLoginState();
    }

    // A click anywhere on the row flips the switch, as a click on a label does. A click on
    // the switch itself is the switch's own and must not count twice.
    private void LoginRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (!LoginSwitch.IsEnabled || LoginSwitch.IsMouseOver) return;
        LoginSwitch.IsChecked = LoginSwitch.IsChecked != true;
        LoginSwitch_Click(LoginSwitch, e);
    }

    private void Status_Click(object sender, RoutedEventArgs e)
    {
        _settings = new ClayoSettings(
            HeaderSwitch.IsChecked == true,
            FooterSwitch.IsChecked == true,
            ModelTick.IsChecked == true,
            BranchTick.IsChecked == true,
            ContextTick.IsChecked == true,
            EffortTick.IsChecked == true,
            FiveHourTick.IsChecked == true,
            SevenDayTick.IsChecked == true);
        ShowStatusPage();
        SettingsChanged?.Invoke(_settings);
    }

    // As LoginRow_Click, for any row whose Tag is its switch.
    private void SwitchRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not CheckBox sw || !sw.IsEnabled || sw.IsMouseOver) return;
        sw.IsChecked = sw.IsChecked != true;
        Status_Click(sw, e);
    }

    /// <summary>
    /// The switches' states, which ticks still mean something, and the previews. A field
    /// whose every placement is off keeps its tick but greys out, and says why.
    /// </summary>
    private void ShowStatusPage()
    {
        bool head = _settings.StatusHeader, foot = _settings.StatusFooter;
        HeaderState.Text = head ? "On" : "Off";
        FooterState.Text = foot ? "On" : "Off";
        foreach (var tick in new[] { ModelTick, BranchTick, ContextTick, EffortTick }) tick.IsEnabled = head;
        foreach (var tick in new[] { FiveHourTick, SevenDayTick }) tick.IsEnabled = head || foot;
        WhyHead.Text = head ? "" : "Pane header is off";
        WhyFoot.Text = !foot && !head ? "Both placements are off"
                     : !foot ? "Shown in the pane header while the footer is off"
                     : "";

        var now = DateTimeOffset.Now;
        var sample = Sample(now);
        PreviewStrip.Show(StatusStrip.For(sample, SampleGit,
            new AccountLimits(sample.FiveHour, sample.SevenDay, sample.Updated), _settings, now));
        NumbersCardStrip.Show(new Strip(null, null, StatusStrip.ContextMeter(sample), null,
            StatusStrip.LimitMeter(true, sample.FiveHour!, now), null), leadRule: false);
    }

    // The design's sample session, so the preview shows every field whatever is open. The
    // half minute keeps the countdowns from reading a minute short.
    private static readonly GitStatus SampleGit = new("feature/upgrade-clayo-v1", 12, 3);

    private static SessionStatus Sample(DateTimeOffset now) => new(
        "00000000-0000-0000-0000-000000000000", "Opus 5.5 (1M context)", null,
        340_000, 1_000_000, 34, "xhigh",
        new Limit(62, now + new TimeSpan(1, 20, 30)), new Limit(14, now + new TimeSpan(4, 10, 0, 30)),
        now.UtcDateTime);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires while InitializeComponent is still building the pages.
        if (PageGeneral is null || PageStatus is null) return;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
