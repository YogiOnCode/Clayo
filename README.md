<div align="center">

<img src="media/icon.png" width="96" alt="Clayo mascot" />

# Clayo

**Every Claude Code and Codex session in one Windows window, plus an island that tells you which one needs you.**

![Latest release](https://img.shields.io/github/v/release/YogiOnCode/Clayo)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

</div>

<!-- Video: drag brag.mp4 into a GitHub comment box, paste the
     https://github.com/user-attachments/assets/… URL on its own line here, and it plays inline. -->

<p align="center"><img src="media/poster.jpg" alt="Clayo launch video" width="800" /></p>

## Why

You run four or five coding agent sessions at once, Claude Code or Codex, each in its own
terminal. One is waiting for a permission, one finished ten minutes ago, and you're in the
browser. Clayo puts them all in one window and tells you when one needs you, without you
switching windows.

## What you get

![The Clayo window: sessions on the left, a branch nested under its parent, the real Claude Code terminal on the right](media/hero.png)

- **The real Claude Code and Codex.** Each pane is a real terminal (ConPTY + xterm.js)
  running the `claude` or `codex` you already use: slash commands, permission prompts, plan
  mode, your status line.
- **Both side by side, or just one.** Use Claude Code, Codex or both; with both, each
  session in the sidebar is tagged **CC** or **CX**.
- **Set up in one screen.** The first time it starts, Clayo finds your agents, installs or
  signs in to the one you're missing, and turns on start at login and `clayo` in Explorer.
- **Sessions on the left, grouped by folder.** Search, rename (F2), resume with a click.
- **Branch from here.** Fork a conversation into a new session. The branch sits under its
  parent; the original stays as it was.
- **The island.** A small pill drops from the top of the screen when a session needs you or
  has finished, even while you're in another app. It never takes focus. Click it to jump to
  that session.

  ![The island drops in when a session needs you, then turns green when one is finished](media/island.gif)

- **Drop a file or folder on the island** and it opens in Clayo.
- **Status at a glance.** Each pane's header shows model, branch and diff, context and
  effort. The sidebar footer shows your 5-hour and 7-day limits, drawn as Numbers, Bars,
  Rings or Chips, and the island warns you once when you get near one.
- **Type `clayo` in any Explorer address bar** to open a session in that folder.
- **Stays out of the way.** Start at login, hidden until the island has something to say.
  Closing the window keeps sessions running.

## Install

Open PowerShell, paste this and press Enter:

```powershell
$a = (irm https://api.github.com/repos/YogiOnCode/Clayo/releases/latest).assets | ? name -like '*-win-x64.zip'; $z = "$env:TEMP\clayo.zip"; irm $a.browser_download_url -OutFile $z; Expand-Archive $z "$env:LOCALAPPDATA\Programs" -Force; Remove-Item $z; & "$env:LOCALAPPDATA\Programs\Clayo\clayo.exe"
```

It downloads the latest release, puts it in `%LOCALAPPDATA%\Programs\Clayo` and starts it.

<details>
<summary>Or by hand</summary>

1. Download `clayo-<version>-win-x64.zip` from the
   [latest release](https://github.com/YogiOnCode/Clayo/releases/latest).
2. Unzip it into `%LOCALAPPDATA%\Programs` (paste that into Explorer's address bar), so you
   have `%LOCALAPPDATA%\Programs\Clayo\clayo.exe`. Keep it there: start at login and `clayo`
   in Explorer point at this folder.
3. Run `clayo.exe`. Clayo isn't signed yet, so Windows may say "Windows protected your PC":
   click **More info**, then **Run anyway**.

</details>

You need Windows 10 or 11, 64-bit, and nothing else first: .NET comes inside the zip, and the
[WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) is already on
Windows 11 and on most Windows 10 PCs.

### First run

The setup window looks for [Claude Code](https://docs.claude.com/en/docs/claude-code) and
[Codex](https://github.com/openai/codex). You need at least one.

- Missing one? **Install** runs its own installer in a PowerShell window you can watch
  (Codex needs [Node.js](https://nodejs.org) first). **Sign in** does the same for signing
  in, and **Locate…** points Clayo at an install in an unusual folder.
- Choose which ones to use and which one **New session** starts.
- Keep **Start Clayo when I sign in** and **Add clayo to my PATH** ticked, then press
  **Open Clayo**.

**Settings › Agents** brings the setup window back. What works with each agent, and how to
install them by hand: [docs/AGENTS.md](docs/AGENTS.md).

### Updating

Quit Clayo, then run the install command again: it puts the latest release over the old one.
Your settings are kept.

### Uninstalling

1. In **Settings › Agents**, untick **Start Clayo when I sign in** and **Add clayo to my
   PATH**, and press **Open Clayo**.
2. **Quit Clayo** from the gear menu.
3. Delete `%LOCALAPPDATA%\Programs\Clayo`, and `%LOCALAPPDATA%\Clayo` too if you want your
   settings gone. Your Claude Code and Codex sessions aren't touched.

### Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/YogiOnCode/Clayo.git
cd Clayo
dotnet publish src/Clayo -c Release -r win-x64 --self-contained false -o $env:LOCALAPPDATA\Programs\Clayo
```

Then run `clayo.exe` from there; the setup window takes care of `PATH`. Keep the `Assets\`
folder next to `clayo.exe`, the terminal pane loads from it. `.\scripts\publish.ps1` builds
the release zip instead. The self-checks in `checks/` need the .NET 10 SDK (they're
file-based apps); the app itself builds with .NET 8.

## Use

- **New session**: the button at the bottom of the sidebar, or `clayo` in a folder.
- **Branch**: open a session, then **Branch from here** in its header.
- **Settings**: the gear menu, or `Ctrl+,`. **Agents** there reopens the setup window.
- **Quit**: **Quit Clayo** in the gear menu or the island's menu. The window's close button
  only hides it.

More in the [wiki](https://github.com/YogiOnCode/Clayo/wiki).

## Docs

| | |
|---|---|
| [Claude Code and Codex](docs/AGENTS.md) | What works with each, installing them by hand |
| [Install](https://github.com/YogiOnCode/Clayo/wiki/Install) | Download, first run, updating, uninstalling |
| [Sessions and Branching](https://github.com/YogiOnCode/Clayo/wiki/Sessions-and-Branching) | What each button runs |
| [The Island](https://github.com/YogiOnCode/Clayo/wiki/The-Island) | Notices, peek, drop to open |
| [Status Bar and Limits](https://github.com/YogiOnCode/Clayo/wiki/Status-Bar-and-Limits) | Pane header, limits, themes |
| [Settings](https://github.com/YogiOnCode/Clayo/wiki/Settings) | Every option |
| [Troubleshooting](https://github.com/YogiOnCode/Clayo/wiki/Troubleshooting) | When something looks wrong |
| [How It Works](https://github.com/YogiOnCode/Clayo/wiki/How-It-Works) | The code, module by module |
| [Roadmap](https://github.com/YogiOnCode/Clayo/wiki/Roadmap) | What's next |

## Contributing

Issues and pull requests are welcome. Open an issue before a big change so we can agree on
the approach first. Pull requests to `main` need one review and are squash-merged. See
[Contributing](https://github.com/YogiOnCode/Clayo/wiki/Contributing) for building and
running the checks. Found a security issue? Report it privately from the **Security** tab,
not in an issue.

## Notes

Not affiliated with Anthropic or OpenAI. Clayo reads `~/.claude` and `~/.codex` and never
writes to either. Where it does keep files is listed in
[How It Works](https://github.com/YogiOnCode/Clayo/wiki/How-It-Works#where-clayo-keeps-its-files).

MIT licensed, see [LICENSE](LICENSE). Bundles [xterm.js](https://github.com/xtermjs/xterm.js)
and its fit and WebGL addons (MIT, see `src/Clayo/Assets/xterm/LICENSE.xterm`).
