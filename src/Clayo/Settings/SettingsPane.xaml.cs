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
        BuildAskCards();
        Chevron(OwnChevron);
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
        // A lone card starts open; with more, all start closed. Your own rules starts closed.
        _openCards.Clear();
        if (AskRules.Groups.Length == 1) _openCards.Add(AskRules.Groups[0].Id);
        _ownOpen = false;
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
    // A card per kind of action (docs/SECURITY.md, "The page"). Ticks and switches save at once, like the others.

    private readonly List<(AskGroup Group, Border Head, CheckBox Switch, TextBlock State, Button Chevron, Border Body)> _askCards = [];
    private readonly List<(AskGroup Group, AskRule Rule, CheckBox Tick, TextBlock Line, Border Claude, Border Codex)> _askRows = [];

    // Which cards are open, and Your own rules. Not saved: each time Settings opens, they start the same way.
    private readonly HashSet<string> _openCards = [];
    private bool _ownOpen;

    private void BuildAskCards()
    {
        foreach (var group in AskRules.Groups)
        {
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), Tag = group.Id };
            AutomationProperties.SetName(sw, group.Title);
            sw.Click += GroupSwitch_Click;
            var state = new TextBlock { Style = (Style)FindResource("SwitchState"), Width = double.NaN };
            var chevron = Chevron(new Button { Margin = new(10, 0, 0, 0) });
            chevron.Click += (_, _) => ToggleCard(group.Id);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(controls, Dock.Right);
            controls.Children.Add(state);
            controls.Children.Add(sw);
            controls.Children.Add(chevron);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Style = (Style)FindResource("RowTitle"), Text = group.Title });
            text.Children.Add(new TextBlock { Style = (Style)FindResource("RowText"), Text = group.Line });
            var row = new DockPanel { Style = (Style)FindResource("Row"), Margin = new(16, 13, 10, 13) };
            row.Children.Add(new TextBlock { Style = (Style)FindResource("RowIcon"), Text = group.Icon });
            row.Children.Add(controls);
            row.Children.Add(text);
            // A click on the header opens or closes the card; one on its switch or chevron is theirs.
            var head = new Border
            {
                Style = (Style)FindResource("SwitchRow"), Child = row, BorderBrush = (Brush)FindResource("Hairline"),
                BorderThickness = new(0, _askCards.Count == 0 ? 0 : 1, 0, 0)
            };
            head.MouseLeftButtonUp += (_, _) => { if (!sw.IsMouseOver && !chevron.IsMouseOver) ToggleCard(group.Id); };

            var list = new StackPanel();
            AutomationProperties.SetName(list, $"{group.Title}: what it covers");
            foreach (var rule in group.Rules) list.Children.Add(AskRow(group, rule));
            var body = new Border
            {
                Child = list, Padding = new(8, 6, 8, 6),
                BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new(0, 1, 0, 0)
            };

            var card = new StackPanel();
            card.Children.Add(head);
            card.Children.Add(body);
            AskCards.Children.Add(card);
            _askCards.Add((group, head, sw, state, chevron, body));
        }
    }

    private UIElement AskRow(AskGroup group, AskRule rule)
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
        _askRows.Add((group, rule, tick, line, cc, cx));
        return row;
    }

    private Border Chip(string agent) =>
        new() { Style = (Style)FindResource("AgentChip"), Child = new TextBlock { Text = agent, Padding = new(0, 2, 0, 2) } };

    // The button that opens and closes a card or Your own rules: a chevron, reached with Tab, pressed with Enter or Space.
    private Button Chevron(Button b)
    {
        b.Style = (Style)FindResource("QuietButton");
        b.BorderThickness = new(0);
        b.Padding = new(0);
        b.Width = b.Height = 28;
        b.FontFamily = (FontFamily)FindResource("Icons");
        b.FontSize = 10;
        b.Foreground = (Brush)FindResource("MutedAA");
        b.VerticalAlignment = VerticalAlignment.Center;
        b.FocusVisualStyle = (Style)FindResource("FocusRing");
        return b;
    }

    private static void ShowChevron(Button b, bool open, string what)
    {
        b.Content = open ? "" : "";
        AutomationProperties.SetName(b, $"{(open ? "Hide" : "Show")} {what}");
    }

    private void ShowSecurity()
    {
        var s = _settings;
        AgentsBox.Visibility = s.UseCodex ? Visibility.Visible : Visibility.Collapsed;
        ClaudeRow.Visibility = s.UseClaude ? Visibility.Visible : Visibility.Collapsed;
        CodexRow.BorderThickness = new(0, s.UseClaude ? 1 : 0, 0, 0);
        CodexRow.CornerRadius = s.UseClaude ? new(0, 0, 7, 7) : new(7);
        CodexSwitch.IsChecked = s.AlsoForCodex;
        CodexState.Text = s.AlsoForCodex ? "On" : "Off";
        CodexText.Text = s.AlsoForCodex
            ? "Applies to Codex outside Clayo too."
            : "Applies to Codex outside Clayo too. Turning it on asks you once.";
        AskLabel.Margin = new(0, s.UseCodex ? 22 : 0, 0, 8);

        for (int i = 0; i < _askCards.Count; i++)
        {
            var (group, head, sw, state, chevron, body) = _askCards[i];
            bool on = !s.IsGroupOff(group.Id), open = _openCards.Contains(group.Id), last = i == _askCards.Count - 1;
            int ticked = group.Rules.Count(r => !s.IsAskOff(r.Id));
            sw.IsChecked = on;
            state.Text = !on ? "Off" : ticked < group.Rules.Length ? $"On · {ticked} of {group.Rules.Length}" : "On";
            AutomationProperties.SetHelpText(sw, state.Text);
            ShowChevron(chevron, open, $"{group.Title} rules");
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            // Off: the list stays readable but can't be clicked, so you see what you'd get back.
            body.IsEnabled = on;
            head.CornerRadius = new(i == 0 ? 7 : 0, i == 0 ? 7 : 0, last && !open ? 7 : 0, last && !open ? 7 : 0);
        }

        bool codexOnly = s.UseCodex && !s.UseClaude;
        foreach (var (group, rule, tick, line, cc, cx) in _askRows)
        {
            bool on = !s.IsGroupOff(group.Id);
            bool yes = !s.IsAskOff(rule.Id);
            tick.IsChecked = yes;
            bool codexGets = yes && s.AlsoForCodex;
            line.Text = rule.Id != "api" ? rule.Line
                : codexOnly ? "every gh api call"
                : codexGets && s.UseCodex ? $"{rule.Line} · Codex: every gh api call"
                : rule.Line;
            cc.Visibility = s.UseClaude ? Visibility.Visible : Visibility.Collapsed;
            cx.Visibility = s.UseCodex ? Visibility.Visible : Visibility.Collapsed;
            cc.Opacity = on && yes ? 1 : 0.4;
            cx.Opacity = on && codexGets ? 1 : 0.4;
            line.Opacity = on ? 1 : 0.6;
            cc.ToolTip = yes ? "Claude: asks first" : "Claude: runs without asking";
            cx.ToolTip = !yes ? "Codex: runs without asking"
                : !codexGets ? "Codex: Also apply to Codex is off"
                : rule.Id == "api" ? "Codex: asks before every gh api call"
                : "Codex: asks first";
        }

        AskFoot.Text = AskRules.Groups.All(g => s.IsGroupOff(g.Id)) ? "Off. Only your own rules apply."
            : codexOnly && !s.AlsoForCodex ? "Codex asks once Also apply to Codex is on. Your own rules still apply."
            : "Applies to sessions you start from now on. Your own rules still apply.";
        ShowOwnRules();
    }

    private void ToggleCard(string id)
    {
        if (!_openCards.Remove(id)) _openCards.Add(id);
        ShowSecurity();
    }

    private void GroupSwitch_Click(object sender, RoutedEventArgs e)
    {
        var sw = (CheckBox)sender;
        _settings = _settings with { AskGroupsOn = Toggled(_settings.AskGroupsOn, (string)sw.Tag, sw.IsChecked == true) };
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
        _settings = _settings with { AskOff = Toggled(_settings.AskOff, (string)tick.Tag, tick.IsChecked != true) };
        ApplySecurity();
    }

    /// <summary>A comma list of ids, as AskOff and AskGroupsOn keep them, with id in it or not.</summary>
    private static string Toggled(string list, string id, bool listed)
    {
        var ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(x => x != id);
        if (listed) ids = ids.Append(id);
        return string.Join(",", ids.Order());
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
        _settings = _settings with { AlsoForCodex = true };
        ApplySecurity();
        CodexSwitch.Focus();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CancelConfirm();

    /// <summary>Closes the Codex confirm if it is open, for Esc. False when there was none to close.</summary>
    public bool CancelConfirm()
    {
        if (CodexConfirm.Visibility != Visibility.Visible) return false;
        CodexConfirm.Visibility = Visibility.Collapsed;
        CodexSwitch.Focus();
        return true;
    }

    private void OwnHead_Click(object sender, MouseButtonEventArgs e)
    {
        if (!OwnChevron.IsMouseOver) OwnChevron_Click(sender, e);
    }

    private void OwnChevron_Click(object sender, RoutedEventArgs e)
    {
        _ownOpen = !_ownOpen;
        ShowOwnRules();
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

        // Closed, the header says how many: "Claude 12 · Codex 3", or None.
        OwnCounts.Text = agents.All(a => a.Rules.Rules.Count == 0) ? "None"
            : string.Join(" · ", agents.Select(a => $"{a.Name.Split(' ')[0]} {a.Rules.Rules.Count}"));
        OwnBody.Visibility = _ownOpen ? Visibility.Visible : Visibility.Collapsed;
        OwnHead.CornerRadius = _ownOpen ? new(7, 7, 0, 0) : new(7);
        ShowChevron(OwnChevron, _ownOpen, "your own rules");

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
