using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
        BuildAskList();
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
        CodexConfirm.Visibility = Visibility.Collapsed;
        ShowSecurity();

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

    // ---------------------------------------------------------------- Security
    // Ask before publishing (docs/PERMISSIONS.md, "The page"). Ticks save at once, like the others.

    private readonly List<(AskRule Rule, CheckBox Tick, TextBlock Line, Border Claude, Border Codex)> _askRows = [];

    // Only Codex in use: the main switch is the Codex one, off until you confirm (D4, D9).
    private bool CodexOnly => _settings.UseCodex && !_settings.UseClaude;

    // Until the page has a card per group (docs/SECURITY.md, S2), the main switch is Publishing's.
    private bool PublishingOn => !_settings.IsGroupOff("publishing");

    private ClayoSettings WithPublishing(bool on)
    {
        var off = _settings.AskGroupsOff.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(x => x != "publishing");
        if (!on) off = off.Append("publishing");
        return _settings with { AskGroupsOff = string.Join(",", off.Order()) };
    }

    private void BuildAskList()
    {
        foreach (var rule in AskRules.All)
        {
            var tick = new CheckBox { Style = (Style)FindResource("Tick"), Content = rule.Label, Tag = rule.Id };
            AutomationProperties.SetHelpText(tick, rule.Line);
            tick.Click += AskTick_Click;
            var line = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(2, 0, 12, 0)
            };
            Border cc = Chip("CC"), cx = Chip("CX");
            cx.Margin = new(4, 0, 0, 0);
            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) };
            chips.Children.Add(cc);
            chips.Children.Add(cx);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(tick);
            Grid.SetColumn(line, 1);
            row.Children.Add(line);
            Grid.SetColumn(chips, 2);
            row.Children.Add(chips);
            AskList.Children.Add(row);
            _askRows.Add((rule, tick, line, cc, cx));
        }
    }

    private Border Chip(string agent) =>
        new() { Style = (Style)FindResource("AgentChip"), Child = new TextBlock { Text = agent, Padding = new(0, 2, 0, 2) } };

    private void ShowSecurity()
    {
        var s = _settings;
        bool on = CodexOnly ? PublishingOn && s.AlsoForCodex : PublishingOn;
        int ticked = AskRules.All.Count(r => !s.IsAskOff(r.Id));
        AskSwitch.IsChecked = on;
        AskState.Text = !on ? "Off" : ticked < AskRules.All.Length ? $"On · {ticked} of {AskRules.All.Length}" : "On";
        AskText.Text = CodexOnly
            ? "Codex stops and asks you before it changes anything on GitHub."
            : "Sessions stop and ask you before they change anything on GitHub. Applies to sessions you start from now on.";
        AskOutside.Visibility = CodexOnly ? Visibility.Visible : Visibility.Collapsed;

        CodexRow.Visibility = s.UseClaude && s.UseCodex ? Visibility.Visible : Visibility.Collapsed;
        CodexSwitch.IsChecked = s.AlsoForCodex;
        CodexSwitch.IsEnabled = on;
        CodexRow.Cursor = on ? Cursors.Hand : Cursors.Arrow;
        CodexState.Text = s.AlsoForCodex ? "On" : "Off";

        // Off: the list stays readable but can't be clicked, so you see what you'd get back.
        AskList.IsEnabled = on;
        foreach (var (rule, tick, line, cc, cx) in _askRows)
        {
            bool yes = !s.IsAskOff(rule.Id);
            tick.IsChecked = yes;
            bool codexGets = yes && (CodexOnly || s.AlsoForCodex);
            line.Text = rule.Id != "api" ? rule.Line
                : CodexOnly ? "every gh api call"
                : codexGets && s.UseCodex ? $"{rule.Line} · Codex: every gh api call"
                : rule.Line;
            cc.Visibility = s.UseClaude ? Visibility.Visible : Visibility.Collapsed;
            cx.Visibility = s.UseCodex ? Visibility.Visible : Visibility.Collapsed;
            cc.Opacity = on && yes ? 1 : 0.4;
            cx.Opacity = on && codexGets ? 1 : 0.4;
            line.Opacity = on ? 1 : 0.6;
            cc.ToolTip = yes ? "Claude: asks first" : "Claude: runs without asking";
            cx.ToolTip = !yes ? "Codex: runs without asking"
                : !codexGets ? "Codex: Also for Codex is off"
                : rule.Id == "api" ? "Codex: asks before every gh api call"
                : "Codex: asks first";
        }

        AskFoot.Text = !on ? "Off. Only your own rules apply."
            : CodexOnly ? "Your own rules still apply."
            : s.UseCodex ? "Claude: only sessions Clayo starts. Your own rules still apply."
            : "Only sessions Clayo starts. Your own rules still apply.";
        ShowOwnRules();
    }

    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        bool want = AskSwitch.IsChecked == true;
        if (CodexOnly)
        {
            if (want) { AskSwitch.IsChecked = false; ShowConfirm(); return; }
            _settings = _settings with { AlsoForCodex = false };
        }
        else _settings = WithPublishing(want);
        ApplySecurity();
    }

    private void Codex_Click(object sender, RoutedEventArgs e)
    {
        if (CodexSwitch.IsChecked == true) { CodexSwitch.IsChecked = false; ShowConfirm(); return; }
        _settings = _settings with { AlsoForCodex = false };
        ApplySecurity();
    }

    private void AskTick_Click(object sender, RoutedEventArgs e)
    {
        var tick = (CheckBox)sender;
        var id = (string)tick.Tag;
        var off = _settings.AskOff.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(x => x != id);
        if (tick.IsChecked != true) off = off.Append(id);
        _settings = _settings with { AskOff = string.Join(",", off.Order()) };
        ApplySecurity();
    }

    private void ApplySecurity()
    {
        ShowSecurity();
        SettingsChanged?.Invoke(_settings);
    }

    private void ShowConfirm()
    {
        CodexConfirm.Visibility = Visibility.Visible;
        ConfirmCancel.Focus();
    }

    private void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        CodexConfirm.Visibility = Visibility.Collapsed;
        _settings = WithPublishing(true) with { AlsoForCodex = true };
        ApplySecurity();
        (CodexOnly ? AskSwitch : CodexSwitch).Focus();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CancelConfirm();

    /// <summary>Closes the Codex confirm if it is open, for Esc. False when there was none to close.</summary>
    public bool CancelConfirm()
    {
        if (CodexConfirm.Visibility != Visibility.Visible) return false;
        CodexConfirm.Visibility = Visibility.Collapsed;
        (CodexOnly ? AskSwitch : CodexSwitch).Focus();
        return true;
    }

    /// <summary>
    /// Your own rules (D7, D8), read each time the page shows and after a tick, for the notes.
    /// Never written: Open file is the one way to them.
    /// </summary>
    private void ShowOwnRules()
    {
        OwnList.Children.Clear();
        var agents = new List<(string Chip, string Name, OwnRules Rules)>();
        if (_settings.UseClaude)
            agents.Add(("CC", "Claude Code", OwnRulesReader.Claude(Path.Combine(StatusRelay.ClaudeConfigDir, "settings.json"), _settings)));
        if (_settings.UseCodex)
            agents.Add(("CX", "Codex", OwnRulesReader.Codex(AskRules.CodexHome, _settings)));

        foreach (var (chip, name, own) in agents)
        {
            var head = new DockPanel { Margin = new(16, 10, 16, 10) };
            if (own.File is { } file)
            {
                var link = new Hyperlink(new Run("Open file")) { Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0x9A, 0x7E)) };
                link.Click += (_, _) => OpenFile(file);
                var open = new TextBlock(link) { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(open, Dock.Right);
                head.Children.Add(open);
            }
            var label = Chip(chip);
            label.Margin = new(0, 0, 10, 0);
            head.Children.Add(label);
            head.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("RowTitle"), VerticalAlignment = VerticalAlignment.Center });

            var body = new StackPanel { Margin = new(0, 4, 0, 4) };
            if (own.Problem is { } problem)
                body.Children.Add(new TextBlock { Text = problem, Style = (Style)FindResource("RowText"), Margin = new(16, 2, 16, 6) });
            foreach (var rule in own.Rules) body.Children.Add(OwnRow(rule));

            var section = new StackPanel();
            section.Children.Add(new Border
            {
                Child = head, BorderBrush = (Brush)FindResource("Hairline"),
                BorderThickness = new(0, OwnList.Children.Count == 0 ? 0 : 1, 0, 1)
            });
            section.Children.Add(body);
            OwnList.Children.Add(section);
        }
    }

    private UIElement OwnRow(OwnRule rule)
    {
        // In the status colours: allow green, ask and prompt blue, deny and forbidden red.
        var (fg, bg) = rule.Decision switch
        {
            "allow" => ("#9FD3B1", "#246BBF8A"),
            "deny" or "forbidden" => ("#EC9C99", "#29D2605F"),
            _ => ("#9CCBEF", "#295AA9E6"),
        };
        var row = new Grid { Margin = new(16, 3, 16, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Border
        {
            CornerRadius = new(4), Background = (Brush)new BrushConverter().ConvertFromString(bg)!,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = rule.Decision, FontSize = 10.5, FontWeight = FontWeights.SemiBold, LineHeight = 18,
                TextAlignment = TextAlignment.Center, Foreground = (Brush)new BrushConverter().ConvertFromString(fg)!
            }
        });
        var text = new TextBlock
        {
            Text = rule.Rule, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11.5,
            Foreground = (Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = rule.Rule
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var note = new TextBlock
        {
            Text = rule.Note, FontSize = 11, Foreground = (Brush)FindResource("NeedsInput"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0)
        };
        Grid.SetColumn(note, 2);
        row.Children.Add(note);
        return row;
    }

    // An agent's own file, in whatever opens it. A .rules file often has no app, so then Explorer shows it.
    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires while InitializeComponent is still building the pages.
        if (PageGeneral is null || PageStatus is null || PageSecurity is null || PageIsland is null) return;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSecurity.Visibility = NavSecurity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = NavIsland.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens setup, then puts the check back on the page still showing.</summary>
    private void Agents_Checked(object sender, RoutedEventArgs e)
    {
        (PageStatus.IsVisible ? NavStatus : PageSecurity.IsVisible ? NavSecurity : PageIsland.IsVisible ? NavIsland : NavGeneral).IsChecked = true;
        SetupRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
