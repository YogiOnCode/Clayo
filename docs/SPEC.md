# Clayo — what we're building

Clayo = **Cla**ude + **Yo**gi. A Windows desktop shell that runs Claude Code sessions side
by side, with branching conversations in a tree on the left and a real terminal on the right.

Not affiliated with Anthropic. Reads `~/.claude/projects`, never writes to it.

## The product in one screen

```
┌────────────────────────────┬──────────────────────────────────────────┐
│  Clayo          C:\Yogi\…  │  ● Fix the ConPTY resize storm     ⑂ ✕   │
│  ┌──────────────────────┐  ├──────────────────────────────────────────┤
│  │ search               │  │                                          │
│  └──────────────────────┘  │  > claude                                │
│                            │                                          │
│  ▾ Clayo                   │  ╭──────────────────────────────────╮    │
│    ● Fix the resize storm  │  │  Claude Code v2.1.257            │    │
│      ↳ try a 120ms coale…  │  ╰──────────────────────────────────╯    │
│      ↳ what if we debounc… │                                          │
│    ○ Vendor xterm.js       │  > the resize handler redraws garbage    │
│                            │    when I drag the splitter              │
│  ▸ HA-Log-Monitoring       │                                          │
│    ○ Phone number reg…     │  ⏺ Looking at terminal.html…             │
│                            │                                          │
│  ＋ New session            │                                          │
└────────────────────────────┴──────────────────────────────────────────┘
```

Left: a **tree**, not a list. Branches sit indented under the session they came from.
Right: the real Claude Code TUI, unmodified.

## Decisions

**A real terminal, not a reimplemented chat UI.** You said "same as the claude code on
terminal", so the right pane is ConPTY + xterm.js showing the genuine TUI. The tradeoff is
worth naming: a terminal in a box will never look as designed as a native chat view, and
"sleek and classy" has to come from the *chrome* around it — the tree, the header, the
titlebar, the type — not from the conversation itself. The alternative (drive `claude`
with `--print --output-format=stream-json` and render messages as native WPF) buys a
beautiful pane and loses the TUI, its slash commands, its permission prompts, and its
plan mode. **Terminal wins for v1.** Revisit only if the chrome-only polish disappoints.

**Clayo owns the branch graph.** Verified: nothing in the transcripts records a fork's
parent. Since `SessionLauncher` already pre-allocates the child's GUID before launching,
Clayo knows the edge at fork time and just has to persist it. One small JSON file under
`%LOCALAPPDATA%\Clayo\`, keyed by session id. This is the piece that makes the tree possible.

**v1 shows live sessions only.** Per your note — the tree holds sessions started since Clayo
opened. The 217 historical transcripts are a later feature, behind a separate "History"
affordance. This also sidesteps the awkward question of what a 6-month-old flat transcript
list is even for.

**Spawn a shell, then type `claude` into it.** Keeps the pane alive after Claude exits,
matching terminal habit. But the current 400 ms fixed delay must go — sniff the shell prompt
out of the byte stream instead. This machine has no `pwsh`, and Windows PowerShell's profile
load can easily exceed 400 ms.

## Milestones

### M0 — Make it actually run
The app builds but the right pane is dead. Nothing else matters until a session spawns.

- [x] Fix `using System.IO;` in `ConPty.cs`
- [ ] Move files into `Core/`, `UI/`, `Assets/` to match what the code and csproj expect
- [ ] Move `terminal.html` → `Assets/terminal.html`; verify it lands in `bin\...\Assets\`
- [ ] Vendor xterm.js + addon-fit into `Assets/xterm/`, swap the 4 CDN URLs
- [ ] Retarget `net10.0-windows` (only the 10.0.400 SDK is installed)
- [ ] Replace the 400 ms delay with prompt detection
- **Done when:** launching Clayo in a folder spawns Claude Code and you can hold a conversation.

### M1 — The branch tree (the actual point)
- [ ] `SessionGraph`: `{ id, parentId, title, folder, startedAt }`, persisted to `%LOCALAPPDATA%\Clayo\graph.json`
- [ ] Record the edge in `Fork_Click` — the child GUID is already known before launch
- [ ] Swap the sidebar `ListBox` for a `TreeView`: folder → root session → branches, indented
- [ ] Live-only default; historical transcripts hidden behind a toggle
- [ ] Titles from the transcript's **last** `ai-title` line, falling back to `last-prompt`
- [ ] Skip `subagents/**` in `SessionStore.Scan()`
- **Done when:** branch a session twice, and both children appear indented under the parent,
  each with its own title, each independently resumable.

### M2 — Getting in
- [ ] Rename everything `ccx` → `clayo`: assembly, namespace, mutex, pipe, virtual host, window title
- [ ] "Open folder…" button in the sidebar (requirement with no code behind it today)
- [ ] Recent folders list
- [ ] `dotnet publish` → `C:\Tools\Clayo`, add to user PATH, drop a `clayo.cmd`
- [ ] Verify: typing `clayo` in Explorer's address bar opens a session in that folder, and a
      second `clayo` elsewhere adds a session to the existing window rather than opening a new one
- **Done when:** `clayo` from any Explorer folder just works.

### M3 — Sleek and classy
- [ ] Custom titlebar (`WindowChrome`), no stock Windows frame
- [ ] Mica / acrylic backdrop on the sidebar, `DwmSetWindowAttribute`
- [ ] Type pass: Segoe UI Variable for chrome, Cascadia Code in the terminal, one consistent scale
- [ ] Real status per session, from OSC title sequences in the byte stream, not the current
      1.2 s output-silence guess — gives a true *needs your input* state
- [ ] Branch edges drawn as hairlines in the tree, not just indentation
- [ ] Motion: pane crossfade, tree expand, status dot transitions — 120–160 ms, nothing bouncy
- **Done when:** it doesn't look like a WPF app.

### Later
Closing panes (they accumulate forever today) · restoring open panes on launch ·
Ctrl+P session switcher, Ctrl+Tab between panes · searching across history ·
per-session notification when Claude needs input

## Two things to know about branching

Both are `claude` behaviours, not Clayo bugs, and the UI should say so:

1. Permissions granted with *"allow for this session"* **do not** carry into a branch.
2. Resuming the same session id in two panes without forking interleaves both streams into
   one unusable transcript. `Sessions_SelectionChanged` already guards this by switching to
   the open pane instead of starting a second one — keep that guarantee in the tree rewrite.
