using System.Diagnostics;
using System.IO;

namespace CcxShell.Core;

/// <summary>A folder's branch and its uncommitted line counts, as the user's status line shows them.</summary>
public sealed record GitStatus(string Branch, int Added, int Deleted);

/// <summary>
/// Branch and `+added −deleted` for the folders of open panes. Each read runs git twice, so it
/// happens off the UI thread, at most once per interval per folder, and never for a folder
/// that is not in a repo.
/// </summary>
public sealed class GitInfo(TimeSpan interval)
{
    private readonly Dictionary<string, (DateTime at, GitStatus? status)> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reading = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Fired on a background thread when a folder's status differs from the last read.</summary>
    public event Action<string, GitStatus?>? Changed;

    public GitStatus? Get(string folder)
    {
        lock (_lock) return _known.GetValueOrDefault(folder).status;
    }

    /// <summary>Starts a read in the background if this folder is due one. Cheap to call often.</summary>
    public void Refresh(string folder)
    {
        lock (_lock)
        {
            if (_reading.Contains(folder)) return;
            if (_known.TryGetValue(folder, out var k) && DateTime.UtcNow - k.at < interval) return;
            _reading.Add(folder);
        }

        Task.Run(() =>
        {
            var status = Read(folder);
            bool changed;
            lock (_lock)
            {
                changed = _known.GetValueOrDefault(folder).status != status;
                _known[folder] = (DateTime.UtcNow, status);
                _reading.Remove(folder);
            }
            if (changed) Changed?.Invoke(folder, status);
        });
    }

    /// <summary>Null when the folder is not in a repo or git cannot be run.</summary>
    public static GitStatus? Read(string folder)
    {
        if (!IsRepo(folder)) return null;
        if (Git(folder, "rev-parse", "--abbrev-ref", "HEAD")?.Trim() is not { Length: > 0 } branch) return null;
        var (added, deleted) = ParseNumstat(Git(folder, "diff", "--numstat", "--no-ext-diff", "--no-textconv") ?? "");
        return new GitStatus(branch, added, deleted);
    }

    /// <summary>
    /// Looks for .git up the tree rather than asking git, so a folder outside any repo costs
    /// no process at all. .git is a file in a worktree or submodule, so either counts.
    /// </summary>
    public static bool IsRepo(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    /// <summary>`added<tab>deleted<tab>path` per file; a binary file shows - for both and counts nothing.</summary>
    public static (int Added, int Deleted) ParseNumstat(string numstat)
    {
        int added = 0, deleted = 0;
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (int.TryParse(parts[0], out var a)) added += a;
            if (int.TryParse(parts[1], out var d)) deleted += d;
        }
        return (added, deleted);
    }

    /// <summary>Git's stdout, or null if it failed or took too long.</summary>
    private static string? Git(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // A folder may be anything you opened, and its .git/config can name a program to run
        // (core.fsmonitor, or a diff driver, which the diff call turns off). Not from our timer.
        foreach (var a in new[] { "-c", "core.fsmonitor=false", "--no-optional-locks" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(folder);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            // Drain both, or a chatty stderr fills its pipe and git never exits.
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? stdout.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
