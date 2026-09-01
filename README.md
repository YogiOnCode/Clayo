# ccx

A Windows shell for Claude Code sessions. Sessions on the left, a real terminal on the right.

Not affiliated with Anthropic. Reads `~/.claude/projects` and never writes to it.

## Status

**Untested.** This was written without a Windows machine to build on, so treat it as a
first draft that compiles by inspection. `Core/ConPty.cs` is the part most likely to
need fixing — see *Where it will break* below.

## Build

Needs the .NET 8 SDK and the WebView2 runtime (already present on current Windows 11).

```
dotnet build -c Release
```

The output is `ccx.exe`.

## Launching it the way you launch PowerShell

1. Publish somewhere stable:
   `dotnet publish -c Release -r win-x64 --self-contained false -o C:\Tools\ccx`
2. Add `C:\Tools\ccx` to your user `PATH`.
3. Type `ccx` in any folder's Explorer address bar.

Explorer runs the command with that folder as its working directory, which is what
`App.OnStartup` reads. A second `ccx` in a different folder hands the path to the window
you already have and exits, instead of opening another window.

To also launch from a PowerShell prompt without blocking it, drop a `ccx.cmd` next to
the exe:

```bat
@echo off
start "" "%~dp0ccx.exe" %*
```

## How it works

```
SessionStore     scans and watches ~/.claude/projects, parses JSONL heads
SessionLauncher  builds the claude command for new / resume / fork
ConPty           P/Invoke wrapper: pipes, pseudoconsole, CreateProcess
PtyProcess       one child process, a read thread, raw byte events
TerminalPane     WebView2 + xterm.js, bridged to a PtyProcess
MainWindow       sidebar, pane switching, status
SingleInstance   mutex + named pipe handoff from Explorer
```

Each pane spawns a **shell**, then types the `claude` command into it. When Claude exits
you land on a prompt instead of the pane dying. Same as your current habit.

Bytes cross the C#/JS bridge base64-encoded and are never decoded on the way. A pipe read
can split a UTF-8 character or an escape sequence in half; xterm.js reassembles them.
Decoding to a string in C# would corrupt both.

`SessionStore` reads the working directory out of the transcript rather than un-mangling
the folder name. The folder name replaces separators with dashes, so a path that contains
a real dash can't be recovered from it. The mangled name is used for display only.

## Forking

`Branch from here` runs:

```
claude --resume <parent> --fork-session --session-id <new-guid>
```

Pre-allocating the child id means the branch is addressable before its transcript exists,
so the sidebar can show it immediately instead of scraping stdout for the new id.

Two things to know: permissions granted with "allow for this session" do **not** carry
over to a branch, and resuming the same session in two panes *without* forking interleaves
both streams into one transcript. `Sessions_SelectionChanged` guards against the second by
switching to an open pane rather than starting a duplicate.

## Where it will break

- **`InitializeProcThreadAttributeList` size probe.** `lpSize` is a `SIZE_T`. It's declared
  here as `ref IntPtr`, which is right on x64 but worth checking first if `CreateProcess`
  fails with 87.
- **`Dispose` ordering.** `ClosePseudoConsole` can block while the client is still attached
  and the output pipe isn't being drained. Current order is terminate, close console, then
  dispose streams. If closing a pane hangs, this is why.
- **Resize storms.** `ResizePseudoConsole` on every observer callback makes the TUI redraw
  garbage. The JS side coalesces to 60 ms; raise it if dragging the splitter looks bad.
- **The 400 ms delay before typing the command.** PowerShell eats input sent before its
  prompt is up. A timer is a guess — sniffing for the prompt in the output stream is the
  real fix.
- **Console encoding.** If box-drawing characters come out as mojibake, add
  `[Console]::OutputEncoding = [Text.Encoding]::UTF8` to your PowerShell profile.

## Not built yet

- Vendored xterm.js. `Assets/terminal.html` loads it from jsDelivr, so the pane is blank
  offline. `npm i @xterm/xterm @xterm/addon-fit`, copy the dist files into
  `Assets/xterm/`, and swap the four CDN URLs.
- Restoring open panes on launch. `SingleInstance` and `SessionStore` are there; a
  `Workspace` module that persists the open set to JSON is the missing piece.
- Real status. The working/idle dot is a 1.2 s output-silence heuristic. Parsing OSC title
  sequences out of the byte stream would give you a true needs-input state.
- Keyboard navigation. Ctrl+P for the session switcher, Ctrl+Tab between panes.
- Closing a pane. Panes accumulate until the window closes.

## Third-party

xterm.js and `@xterm/addon-fit` are MIT. Nothing else is vendored.
