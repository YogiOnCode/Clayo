# Clayo — what we actually have

Audit date: 2026-09-01. Everything below was verified on this machine, not read off the
existing `README.md` (which describes a folder layout and a feature set that don't exist yet).

## Verdict

A competent first draft of the **hard part** (running a real terminal inside a WPF window)
and almost none of the **product** (the branch tree, folder entry, live-session list).

It now compiles and produces `ccx.exe`. It would launch to a working sidebar and a **blank
black right pane**, because the terminal's HTML never reaches the output folder.

## Inventory

15 files, flat in the repo root. There is no git repo here.

| File | Lines | What it is | State |
|---|---|---|---|
| `ConPty.cs` | ~300 | P/Invoke: pipes, pseudoconsole, `CreateProcessW`, read thread | Plausible, unproven at runtime |
| `TerminalPane.xaml(.cs)` | ~200 | WebView2 host, base64 byte bridge to xterm.js | Sound design, broken asset path |
| `terminal.html` | ~180 | xterm.js front end, resize coalescing, copy/paste | Never loaded (see below) |
| `SessionStore.cs` | ~230 | Scans + watches `~/.claude/projects`, parses JSONL | Works, but wrong for our v1 |
| `SessionLauncher.cs` | ~100 | Builds the `claude` command for new/resume/fork | Correct, flags verified |
| `MainWindow.xaml(.cs)` | ~300 | Sidebar, pane switching, status dot | No branch tree |
| `SingleInstance.cs` | ~110 | Mutex + named pipe handoff from Explorer | Looks right, untested |
| `App.xaml(.cs)` | ~180 | Startup, cwd detection, dark brush palette | Fine |
| `CcxShell.csproj`, `app.manifest` | — | net8.0-windows, WPF, WebView2 1.0.2903.40 | Builds |
| `README.md` | — | Describes `Core/`, `UI/`, `Assets/` subfolders | Fiction — files are flat |

## Environment (verified)

| Thing | Result |
|---|---|
| .NET SDK | **10.0.400 only.** No 8.0 SDK. The net8.0 targeting pack restored from NuGet, so it builds. |
| `claude` CLI | `C:\Users\yoges\.local\bin\claude.exe`, **v2.1.257** |
| `--fork-session`, `--session-id`, `--resume` | All three exist and mean what `SessionLauncher` assumes |
| `pwsh` | **Not installed.** Falls back to `powershell.exe` — see risk below |
| Node | v25.9.0 (available for vendoring xterm.js) |
| Transcripts | 217 `.jsonl` under `~\.claude\projects`, of which **177 are real sessions** and ~40 are `subagents/agent-*.jsonl` |

## Bugs found

1. **`ConPty.cs` did not compile** — missing `using System.IO;` for `FileStream`.
   *Fixed:* added line 1. Build now succeeds, 0 warnings, 0 errors.

2. **The terminal pane is blank.** `TerminalPane.xaml.cs:92` maps the virtual host to
   `<output>\Assets`, and the csproj only copies `Assets\**\*`. But `terminal.html` sits in
   the repo root, so nothing lands in the output folder. Confirmed: `bin\Debug\net8.0-windows\`
   has no `Assets` directory at all.

3. **xterm.js comes from a CDN.** `terminal.html` `<script src="https://cdn.jsdelivr.net/...">`.
   Offline, or behind a proxy, the pane stays blank even once #2 is fixed.

4. **Subagent transcripts are counted as sessions.** `SessionStore.cs:53` uses
   `SearchOption.AllDirectories`, which sweeps in `subagents/agent-*.jsonl`. That is 40 phantom
   rows out of 217.

5. **The 400 ms guess is a real risk here.** `TerminalPane.xaml.cs:169` waits 400 ms, then types
   `claude` into the shell. That was tuned for `pwsh`. This machine only has Windows PowerShell,
   whose profile load is routinely slower — if it is, the command is typed into the void and
   the pane just sits at a prompt.

## What the transcripts actually contain

Worth knowing, because it changes the design:

- **`{"type":"ai-title","aiTitle":"..."}`** — Claude Code writes a real generated title into
  every transcript, e.g. *"Debug phone number registration failures"*. `SessionStore` doesn't
  read it, and instead shows a truncated first prompt. The title is far better sidebar text.
  Note it is **appended repeatedly** as the conversation evolves, so you want the *last*
  occurrence — a 40-line head read won't find it.
- **`{"type":"last-prompt","lastPrompt":"..."}`** — the most recent prompt. Good second line.
- **`sessionId` is on line 1**; `cwd` appears on the first `user`/`assistant` line.
- **No fork/parent field exists.** I scanned all 177 transcripts for a shared first message
  uuid and found **zero** groups. Nothing on disk records that session B was branched from
  session A. **Clayo has to own that graph itself.**

## Gaps against what you asked for

| You asked for | Status |
|---|---|
| Branch off a chat, nested under the chat it came from | **Missing.** `Fork_Click` (`MainWindow.xaml.cs:158`) runs the right command, but nothing stores the parent link and the sidebar is a flat `ListBox` grouped by folder (`:116`) |
| Left pane holds sessions since Clayo opened | **Wrong.** It scans all 217 historical transcripts instead |
| Open a session for a folder, chosen from inside Clayo | **Missing.** No folder picker anywhere; `AdoptFolder` only fires from the Explorer pipe |
| Type `clayo` in Explorer's address bar | **Plumbing exists, name doesn't.** Everything is `ccx` — exe, mutex, pipe, virtual host. Nothing is installed on PATH |
| Right pane behaves like Claude Code in a terminal | **Right approach, not yet running** (blocked on #2 and #3) |
| Sleek and classy | **Decent bones.** Graphite palette, one amber accent, restrained list styling. Still stock WPF chrome, no titlebar work, no motion |
