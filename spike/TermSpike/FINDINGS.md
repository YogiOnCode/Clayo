# Native terminal pane — Windows findings (M2a)

2026-10-21, Windows 11, Intel integrated graphics, Avalonia 12.1.4, Debug builds. Two native
terminal controls, run through the same tests (`--lib tav` / `--lib ice`), against Clayo 1.0.6 + #32
(WPF, WebView2, xterm.js with WebGL).

| | Clayo (WebView2) | Terminal.Avalonia 0.4.4 (`tav`) | Iciclecreek.Avalonia.Terminal 4.1.0 (`ice`) |
|---|---|---|---|
| Licence | — | MIT | MIT |
| Source | — | **not public** (repo 404, web and API) | **public**: tomlm, 32★, updated 2026-10-14; XTerm.NET 39★, Porta.Pty 34★ |
| Memory, 4 idle Claude panes | ~456–500 MB | **~150 MB** | **~176 MB** |
| Processes | clayo + 8 WebView2 | 1 + console hosts (7 MB) | 1 + console hosts (7 MB) |
| Switching 10 rounds | paints at once | no growth | no growth |
| 20,000 coloured lines, app CPU | — | 2.7 s | 3.8 s |
| Claude Code: spinner, streaming, permission dialog, diff | ✓ | ✓ | ✓ |
| Colour, CJK width, emoji, box drawing, bold | ✓ | ✓ | ✓ |
| Block characters (mascot) | ✓ | thin seams between cells | ✓ solid |
| Resize | ✓ | ✓ | ✓ |
| Copy | Ctrl+C with a selection | Ctrl+Shift+C only | Ctrl+C with a selection, Ctrl+Shift+C |
| Paste (Ctrl+V) | ✓ | ✓ | ✓ in `ShortcutMode.Desktop` (default mode leaves Ctrl+V to the program) |

At the narrowest width both controls leave one stale line under Claude Code's footer until the next
resize, so that is Claude Code's redraw, not either control.

## Things Clayo would have to do (ice)

- **Focus the inner view.** `TerminalControl.Focus()` doesn't give the terminal the keys; the
  focusable `TerminalView` inside does. Without it typed text goes nowhere.
- **Start every pane itself.** The control launches its process on `Loaded`, and a hidden control
  isn't loaded, so a pane created hidden doesn't start its shell until first shown. Clayo starts
  sessions that may stay in the background: call `LaunchProcess()` explicitly.
- **`[STAThread]` on Main**, as any Avalonia app on Windows: the clipboard fails without it
  (CO_E_NOTINITIALIZED). Found with tav, applies to both.
- Status detection reads the raw output: ice has `OutputReceived`, tav `RawOutputReceived`. Unused here.

## Download size

Self-contained (no .NET needed on the PC), `-p:SatelliteResourceLanguages=en`, both terminal
libraries still in. SkiaSharp's 100 MB of `.pdb` symbols and the arm64/x86 console hosts removed,
as a release would.

| Build | On disk | Zipped |
|---|---|---|
| Clayo 1.0.6 today (WPF + WebView2 assets) | — | 59 MB |
| Windows x64, untrimmed | 107 MB | 46 MB |
| **Windows x64, trimmed** | 46 MB | **20 MB** |
| macOS arm64, untrimmed | 113 MB | 43 MB |
| **macOS arm64, trimmed** | 46 MB | **19 MB** |

- Trimming removed Iciclecreek's compiled theme (`Themes/Generic.axaml`) and the app crashed at
  start; `<TrimmerRootAssembly Include="Iciclecreek.Avalonia.Terminal" />` fixes it. With that, the
  trimmed Windows build ran 4 Claude panes correctly (~172 MB). The trimmed Mac build was only
  built here, not run.
- Biggest files: libSkiaSharp (11–15 MB), the .NET runtime (~10 MB), Avalonia (~5 MB).
- Not tried: NativeAOT, which would drop the JIT and more.

## Not tested

- Pasting images, OSC 8 links, typing latency by feel. (IME: not needed.)
- macOS. ice uses Porta.Pty there, tav uses `forkpty`; the Mac test (M2b) has to show the pseudo-
  terminal works in a .NET process on a Mac.

## Verdict

Go native, with **Iciclecreek.Avalonia.Terminal**: open source end to end (control, emulator and
PTY, one author, MIT), draws Claude Code correctly, and copy/paste already behave as Clayo's do.
It costs ~26 MB and some CPU more than Terminal.Avalonia, which can't be adopted while its source
isn't public. Either way about a third of today's memory.
