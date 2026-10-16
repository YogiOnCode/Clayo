namespace CcxShell.Core;

/// <summary>
/// The transcripts a watcher saw change since the last scan, so a scan reads only those.
/// Listing every transcript on each refresh cost ~16 ms of the UI thread at 220 sessions, and
/// grew with history (issue #21).
/// </summary>
internal sealed class ChangedFiles
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _all = true;

    public void Add(string path) { lock (_paths) _paths.Add(path); }

    /// <summary>The watcher lost track (its buffer overflowed), so the next scan looks at every file.</summary>
    public void AddAll() { lock (_paths) _all = true; }

    /// <summary>
    /// What a scan should look at now: every file until a watcher runs, and once more on its
    /// first scan, which covers what changed before it started; after that, what it reported.
    /// </summary>
    public (bool All, List<string> Paths) Take(bool watching)
    {
        lock (_paths)
        {
            var taken = (_all || !watching, _paths.ToList());
            _paths.Clear();
            _all = !watching;
            return taken;
        }
    }
}
