# Claude Code and Codex in Clayo

Clayo runs your coding agent in its panes: Claude Code, Codex, or both. You need at least
one. The first time Clayo starts, its setup window finds them, installs or signs in to one
if you ask, and lets you choose which to use. **Settings › Agents** opens it again.

Checked with Claude Code 2.1.288 and codex-cli 0.160.0 on Windows 11.

## What works with each

| | Claude Code | Codex |
|---|---|---|
| Found and set up by the setup window | Yes | Yes (Install needs Node.js) |
| New session | Yes | Yes |
| Resume a session | Yes | Yes |
| Branch from here | Yes | Yes |
| Sessions in the sidebar, by project | Yes | Yes |
| Session titles | Claude's own | Codex's own |
| Branches shown under the session they came from | Branches made in Clayo | Every fork, also ones made outside Clayo |
| Status light: working, done | Yes | Yes |
| Status light: needs you | Yes | Yes |
| Pane header: model, branch, context, effort | Yes | Yes (effort only when you set one) |
| Usage limits | In the sidebar footer | In the pane header, for 5-hour and 7-day limits |
| Island notices (needs you, done) | Yes | Yes |
| Drop a file on the island | Yes | Yes |

With both in use, each sidebar row is tagged **CC** or **CX**, and **New session** starts
the one you picked in setup (or asks each time).

### Where they differ

- **A new Codex session shows in the sidebar after your first message.** Codex picks its
  own session id and writes nothing until then, so Clayo finds the session once it appears.
  Claude takes the id Clayo gives it, so its row is there at once.
- **Codex limits.** Codex reports its limits per plan. A 5-hour or 7-day limit shows in the
  Codex pane's header; a plan whose only limit has another length (a monthly one, for
  example) shows none.
- **A Codex branch needs the session it came from.** Codex doesn't copy the history into
  the branch, it points back at the original. Deleting the original's file by hand breaks
  its branches.

## Installing by hand

Use this if the setup window can't install an agent for you. Run the commands in
PowerShell, then press **Check again** in the setup window.

### Claude Code

```powershell
irm https://claude.ai/install.ps1 | iex   # installs %USERPROFILE%\.local\bin\claude.exe
claude auth login                          # sign in once
```

Check it with `claude --version` and `claude auth status`.

### Codex

Codex installs through npm, so it needs [Node.js](https://nodejs.org) first.

```powershell
npm install -g @openai/codex   # installs %APPDATA%\npm\codex.cmd
codex login                    # sign in once
```

Check it with `codex --version` and `codex login status`.

### Installed somewhere else?

Press **Locate…** on the agent's row in the setup window and pick its `claude.exe` or
`codex.cmd`. Otherwise Clayo looks on your PATH, then in the folders the installers above
use (and `%LOCALAPPDATA%\Microsoft\WinGet\Links`).

## Pages

Ask a session for a page and it makes one: a report, a dashboard, a diagram or a slide deck,
as an HTML file shown beside the terminal like a markdown doc. Works with Claude Code and
Codex.

- **Ask for one.** Type `/page <what you want>` in a pane, or `/page` alone for a page of
  what you just discussed. The **Page** button in the pane header types `/page ` for you.
  In Claude you see only your `/page` line; Codex gets one short line.
- **Change one.** With a page open, `/page make the title shorter` edits that page instead
  of making a new one.
- **Offer pages when useful.** In the Page button's menu. The session keeps answering in
  chat and, when a reply would be clearer as a page, asks "Want this as a page?". It makes
  one only if you say yes. Per session.
- **Where they live.** `<project>/.clayo/pages/<name>.html`, beside the guide and
  templates Clayo copies into `.clayo/`. Clayo adds `.clayo/` to `.git/info/exclude`, so git
  ignores it. In Claude, `/page` is a project command in `.claude/commands/page.md`, also
  excluded; a `page.md` of your own is never overwritten. A page also opens straight in a
  browser.
- **What a page can do.** Text, tables, charts, Mermaid diagrams and slides, light and dark.
  **What it can't:** it runs sealed off, so it can't reach Clayo, your files or the network.
  Links ask before they open in your browser.
- **Slides.** A deck gets ← →, a slide counter and **▶ Present** (full screen, Esc to leave).
- **Export.** **Export › PDF / PNG** in the viewer bar. A deck exports one slide per PDF page,
  or one PNG per slide.
- **Themes.** **Settings › Pages** picks one of five looks. Every page follows, old and new,
  on screen, in Present and in exports.
- **The Pages view.** **Pages** at the bottom of the sidebar shows every page from every
  session. Search, filter by project, agent, session, Shared or Slides, and sort. Right-click
  a page (or a selection) to open it, go to its session, export, share, show it in Explorer,
  or delete it (to the Recycle Bin).
- **Share.** **Share** in the viewer bar or on a card:
  - **Save a copy…** writes one file with everything inlined. It opens anywhere, offline.
  - **Create public link…** puts the page in a secret GitHub gist and copies a preview
    link. Anyone with the link can view it. Clayo first checks the page for tokens, keys,
    local paths and internal hosts, and lists what it finds before you confirm. The first
    time, you sign in to the **Clayo Pages** GitHub App with a code: it can reach your gists
    and nothing else. Clayo never uses your `gh` or Git login. **Settings › Pages › Public
    links › Disconnect** signs out and opens GitHub to revoke it.
  - **Update public link** sends the new version to the same link. **Remove public link…**
    deletes the gist. The link shows an update, or stops working, within a minute or two.
    Pages with diagrams are too large for that link and use another preview service, which
    keeps its own copy for a while, so there an update or a removal can take some time to show.

## What Clayo reads

- Claude Code sessions: `%USERPROFILE%\.claude\projects`
- Codex sessions: `%CODEX_HOME%\sessions` if you set `CODEX_HOME`, else
  `%USERPROFILE%\.codex\sessions`

Clayo only reads these, and never opens either agent's sign-in files: to see whether you're
signed in it asks the agent (`claude auth status`, `codex login status`).
