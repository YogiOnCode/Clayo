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

## What Clayo reads

- Claude Code sessions: `%USERPROFILE%\.claude\projects`
- Codex sessions: `%CODEX_HOME%\sessions` if you set `CODEX_HOME`, else
  `%USERPROFILE%\.codex\sessions`

Clayo only reads these, and never opens either agent's sign-in files: to see whether you're
signed in it asks the agent (`claude auth status`, `codex login status`).
