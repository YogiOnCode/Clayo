using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>Agents was picked. MainWindow opens the setup window.</summary>
    public event Action? SetupRequested;

    /// <summary>Update was pressed. MainWindow runs its sidebar Update button's path.</summary>
    public event Action? UpdateRequested;

    private ClayoSettings _settings = new();

    public SettingsPane()
    {
        InitializeComponent();
        foreach (var theme in Enum.GetValues<StatusTheme>())
            AddChoice(ThemeChoices, theme, theme.ToString());
        foreach (var step in ClayoSettings.ReserveSteps)
            AddChoice(ReserveSteps, step, step == 0 ? "Off" : $"{step}%");
    }

    private void AddChoice(Panel group, object value, string label)
    {
        var button = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = group.Name, Tag = value, Content = label };
        button.Click += Status_Click;
        group.Children.Add(button);
    }

    private static T Chosen<T>(Panel group) => (T)group.Children.OfType<RadioButton>().First(b => b.IsChecked == true).Tag;

    private static void Choose(Panel group, object value)
    {
        foreach (var b in group.Children.OfType<RadioButton>()) b.IsChecked = b.Tag.Equals(value);
    }

    /// <summary>
    /// Brings the pages up to date each time Settings opens: the login entry can be changed
    /// from the gear and island menus, or removed outside Clayo, while it was closed.
    /// </summary>
    public void Refresh(ClayoSettings settings, bool shotKeyTaken = false, Version? newer = null)
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
        Choose(ThemeChoices, settings.Theme);
        Choose(ReserveSteps, settings.ReserveAt);
        ReserveFiveHourTick.IsChecked = settings.ReserveFiveHour;
        ReserveSevenDayTick.IsChecked = settings.ReserveSevenDay;
        ShowStatusPage();
        ShotKeySwitch.IsChecked = settings.ScreenshotHotkey;
        ShotOfferSwitch.IsChecked = settings.ScreenshotOffer;
        ShowShots(shotKeyTaken);
        UpdateCheckSwitch.IsChecked = settings.UpdateCheck;
        UpdateCheckState.Text = settings.UpdateCheck ? "On" : "Off";
        ShowUpdate(newer);

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

    private void Status_Click(object sender, RoutedEventArgs e)
    {
        // With, so what the setup window chose is kept.
        _settings = _settings with
        {
            StatusHeader = HeaderSwitch.IsChecked == true,
            StatusFooter = FooterSwitch.IsChecked == true,
            StatusModel = ModelTick.IsChecked == true,
            StatusBranch = BranchTick.IsChecked == true,
            StatusContext = ContextTick.IsChecked == true,
            StatusEffort = EffortTick.IsChecked == true,
            StatusFiveHour = FiveHourTick.IsChecked == true,
            StatusSevenDay = SevenDayTick.IsChecked == true,
            ReserveAt = Chosen<int>(ReserveSteps),
            ReserveFiveHour = ReserveFiveHourTick.IsChecked == true,
            ReserveSevenDay = ReserveSevenDayTick.IsChecked == true,
            Theme = Chosen<StatusTheme>(ThemeChoices)
        };
        ShowStatusPage();
        SettingsChanged?.Invoke(_settings);
    }

    private void Shots_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            ScreenshotHotkey = ShotKeySwitch.IsChecked == true,
            ScreenshotOffer = ShotOfferSwitch.IsChecked == true,
        };
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>The switches' states, and whether Ctrl+Alt+S could be had. MainWindow calls it again after a change.</summary>
    public void ShowShots(bool shotKeyTaken)
    {
        ShotKeyState.Text = _settings.ScreenshotHotkey ? "On" : "Off";
        ShotOfferState.Text = _settings.ScreenshotOffer ? "On" : "Off";
        ShotKeyText.Text = _settings.ScreenshotHotkey && shotKeyTaken
            ? "Another app has Ctrl+Alt+S, so the shortcut is off. Close that app's shortcut, then switch this off and on."
            : "Take a screenshot (Win+Shift+S), then press Ctrl+Alt+S: the island asks which session it is for.";
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { UpdateCheck = UpdateCheckSwitch.IsChecked == true };
        UpdateCheckState.Text = _settings.UpdateCheck ? "On" : "Off";
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>
    /// This Clayo's version, and the newer one the last check found (null: none). MainWindow calls
    /// it after a check, and with waiting while Update waits for busy sessions: pressing again calls it off.
    /// </summary>
    public void ShowUpdate(Version? newer, bool waiting = false)
    {
        VersionTitle.Text = $"Clayo {Updater.Current.ToString(3)}";
        WhatsNew.Visibility = newer is null ? Visibility.Visible : Visibility.Collapsed;
        NewerText.Visibility = UpdateButton.Visibility = newer is null ? Visibility.Collapsed : Visibility.Visible;
        NewerText.Text = newer is null ? ""
            : waiting ? $"Clayo {newer.ToString(3)} installs as soon as no session is working."
            : $"Clayo {newer.ToString(3)} is out.";
        UpdateButton.Content = waiting ? "Call off" : "Update";
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Updater.ReleaseUrl(Updater.Current)) { UseShellExecute = true })?.Dispose();

    private void Update_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();

    // A click anywhere on a row whose Tag is its switch flips the switch, as a click on a
    // label does, and runs the switch's own Click. A click on the switch itself is the
    // switch's own and must not count twice.
    private void SwitchRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not CheckBox sw || !sw.IsEnabled || sw.IsMouseOver) return;
        sw.IsChecked = sw.IsChecked != true;
        sw.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, sw));
    }

    /// <summary>
    /// The switches' states and which ticks still mean something. A field whose every
    /// placement is off keeps its tick but greys out, and says why.
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
        // With no reserve there is nothing for the ticks to watch.
        ReserveFiveHourTick.IsEnabled = ReserveSevenDayTick.IsEnabled = _settings.ReserveAt > 0;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires while InitializeComponent is still building the pages.
        if (PageGeneral is null || PageStatus is null || PageIsland is null) return;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = NavIsland.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens setup, then puts the check back on the page still showing.</summary>
    private void Agents_Checked(object sender, RoutedEventArgs e)
    {
        (PageStatus.IsVisible ? NavStatus : PageIsland.IsVisible ? NavIsland : NavGeneral).IsChecked = true;
        SetupRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
