using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CcxShell.Core;

public enum AgentKind { Claude, Codex }

/// <summary>
/// What setup found for one agent. Exe is null when nothing was found or what was found does
/// not run. Npm is for Codex's Install only (it installs through npm); null for Claude.
/// </summary>
public sealed record AgentInfo(
    AgentKind Kind,
    string? Exe,
    string? Version,
    bool SignedIn,
    string DataFolder,
    int Sessions,
    int Projects,
    string? Npm);

/// <summary>
/// Finds Claude Code and Codex the way docs/SETUP.md "Detecting agents" lays out. The
/// environment and the process runner are passed in so checks/agents.cs can fake both; the
/// file system is real, and the check points the environment at a temp folder.
/// </summary>
public sealed class AgentDetector(Func<string, string?> env, Func<string, string, string?> run)
{
    public static AgentDetector ForThisMachine() => new(Environment.GetEnvironmentVariable, (exe, args) => Run(exe, args));

    /// <summary>Both agents at once, off the calling thread. Saved paths come from "Locate…".</summary>
    public Task<AgentInfo[]> DetectAsync(string? claudePath = null, string? codexPath = null) =>
        Task.WhenAll(
            Task.Run(() => Detect(AgentKind.Claude, claudePath)),
            Task.Run(() => Detect(AgentKind.Codex, codexPath)));

    public AgentInfo Detect(AgentKind kind, string? savedPath = null)
    {
        bool claude = kind == AgentKind.Claude;
        var exe = Locate(kind, savedPath);

        // Found is not enough: a half-removed install still leaves its shim behind.
        var version = exe is null ? null : FirstLine(run(exe, "--version"));
        if (version is null) exe = null;

        bool signedIn = exe is not null && (claude
            ? ClaudeSignedIn(run(exe, "auth status"))
            : CodexSignedIn(run(exe, "login status")));

        var data = claude
            ? env("CLAUDE_CONFIG_DIR") ?? Home(".claude")
            : env("CODEX_HOME") ?? Home(".codex");
        var (sessions, projects) = claude ? CountClaude(data) : CountCodex(data);

        var npm = claude ? null : Find(null, ["npm.cmd"], [In("ProgramFiles", "nodejs", "npm.cmd")]);
        return new AgentInfo(kind, exe, version, signedIn, data, sessions, projects, npm);
    }

    /// <summary>Where the agent is, without running it: cheap enough for startup.</summary>
    public string? Locate(AgentKind kind, string? savedPath = null) => Find(savedPath,
        kind == AgentKind.Claude ? ["claude.exe", "claude.cmd"] : ["codex.cmd", "codex.exe"], KnownPlaces(kind));

    /// <summary>
    /// Runs exe with args and returns what it printed (stdout, then stderr: Codex reports its
    /// login on stderr), or null if it failed, exited non-zero or took longer than the timeout.
    /// </summary>
    public static string? Run(string exe, string args, int timeoutMs = 3000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? output.Result + error.Result : null;
        }
        catch (Win32Exception) { return null; }
    }

    /// <summary>Saved path, then PATH, then the places installers put it. First hit wins.</summary>
    private string? Find(string? savedPath, string[] names, IEnumerable<string?> knownPlaces)
    {
        if (savedPath is not null && File.Exists(savedPath)) return savedPath;
        foreach (var name in names)
            if (SessionLauncher.FindOnPath(name, env("PATH")) is { } onPath) return onPath;
        return knownPlaces.FirstOrDefault(p => p is not null && File.Exists(p));
    }

    private IEnumerable<string?> KnownPlaces(AgentKind kind) => kind == AgentKind.Claude
        ? [Home(".local", "bin", "claude.exe"), In("APPDATA", "npm", "claude.cmd"), In("LOCALAPPDATA", "Microsoft", "WinGet", "Links", "claude.exe")]
        : [In("APPDATA", "npm", "codex.cmd"), In("LOCALAPPDATA", "Microsoft", "WinGet", "Links", "codex.exe")];

    private string Home(params string[] parts) => Path.Combine([env("USERPROFILE") ?? "", .. parts]);

    private string? In(string variable, params string[] parts) =>
        env(variable) is { Length: > 0 } root ? Path.Combine([root, .. parts]) : null;

    private static string? FirstLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>`claude auth status` prints JSON with loggedIn.</summary>
    public static bool ClaudeSignedIn(string? output)
    {
        int start = output?.IndexOf('{') ?? -1;
        if (start < 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(output![start..]);
            return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>`codex login status` prints "Logged in using ChatGPT" (or an API key), else "Not logged in".</summary>
    public static bool CodexSignedIn(string? output) =>
        output is not null && output.Split('\n').Any(l => l.Trim().StartsWith("Logged in", StringComparison.Ordinal));

    /// <summary>projects\&lt;folder&gt;\&lt;id&gt;.jsonl: one folder per project, one file per session.</summary>
    private static (int sessions, int projects) CountClaude(string data)
    {
        var root = Path.Combine(data, "projects");
        if (!Directory.Exists(root)) return (0, 0);
        int sessions = 0, projects = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            int n = Directory.EnumerateFiles(dir, "*.jsonl").Count();
            sessions += n;
            if (n > 0) projects++;
        }
        return (sessions, projects);
    }

    /// <summary>sessions\YYYY\MM\DD\rollout-*.jsonl; the project is session_meta's cwd on line 0.</summary>
    private static (int sessions, int projects) CountCodex(string data)
    {
        var root = Path.Combine(data, "sessions");
        if (!Directory.Exists(root)) return (0, 0);
        int sessions = 0;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            sessions++;
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                if (reader.ReadLine() is not { } line) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("payload", out var p)
                    && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } dir)
                    folders.Add(Path.TrimEndingDirectorySeparator(dir));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return (sessions, folders.Count);
    }
}
