using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CcxShell.Core;
using CcxShell.UI;

namespace CcxShell;

/// <summary>
/// Share for a page, in the viewer bar and on a card (docs/PAGES.md, P7): save a copy, make or
/// update a public link (a secret gist), copy it, or take it down.
/// </summary>
public static class ShareFlow
{
    private static readonly PageShares Shares = new();

    /// <summary>The Share menu's items for a page. changed runs after a link is made or taken down.</summary>
    public static IEnumerable<MenuItem> Items(Window? owner, string page, Action? changed = null)
    {
        MenuItem Item(string header, Func<Task> act)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => { await act(); changed?.Invoke(); };
            return item;
        }

        yield return Item("Save a copy…", () => TerminalPane.SaveCopy(owner, page));
        if (Shares.Get(page) is { } share)
        {
            yield return Item("Copy public link", () => { Copy(share.Url); return Task.CompletedTask; });
            yield return Item("Update public link", () => Link(owner, page));
            yield return Item("Remove public link…", () => Unshare(owner, page, ask: true));
        }
        else yield return Item("Create public link…", () => Link(owner, page));
    }

    public static bool IsShared(string page) => Shares.Get(page) is not null;

    private static MessageBoxResult Ask(Window? owner, string text, MessageBoxButton buttons, MessageBoxImage image = MessageBoxImage.Question) =>
        owner is null ? MessageBox.Show(text, "Clayo", buttons, image) : MessageBox.Show(owner, text, "Clayo", buttons, image);

    // The clipboard can be held by another app for a moment; the link is shown either way.
    private static bool Copy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (COMException) { return false; }
    }

    /// <summary>
    /// The token and its account: Clayo's own sign-in to the Clayo Pages app, which reaches gists
    /// and nothing else (docs/GITHUB-SIGNIN.md). Never a login the user has for other things (gh,
    /// git). Not signed in: the sign-in window. Null when they didn't sign in.
    /// </summary>
    private static async Task<(string Token, string User)?> Login(Window? owner, string page, bool offerCopy)
    {
        if (await Own() is { } own) return own;
        if (new GitHubSignInWindow(owner).ShowDialog() == true && await Own() is { } signedIn) return signedIn;
        if (offerCopy && Ask(owner, "No public link without signing in. Save a copy of the page instead?", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await TerminalPane.SaveCopy(owner, page);
        return null;
    }

    /// <summary>The sign-in, renewed when it ran out; null when there is none or GitHub no longer takes it.</summary>
    private static async Task<(string Token, string User)?> Own()
    {
        if (await GitHubSignIn.Current() is not { } token) return null;
        try { return (token.Access, await GitHubGist.User(token.Access)); }
        catch (GitHubException e) when (e.Status == HttpStatusCode.Unauthorized) { GitHubSignIn.Forget(); return null; }
    }

    // An account GitHub won't let make gists, which it answers with "Not Found" or "Forbidden".
    private static void NoGist(Window? owner) =>
        Ask(owner, "This GitHub account can't make gists. Some company-managed accounts can't. Save a copy of the page instead, or sign in with another account (Settings › Pages › Disconnect).",
            MessageBoxButton.OK, MessageBoxImage.Warning);

    // GitHub says "not found" for a gist that is another account's, so Clayo only touches a link
    // with the account that made it: never a new gist that orphans the old one.
    private static bool OtherAccount(Window? owner, PageShare share, string user)
    {
        if (string.Equals(share.Owner, user, StringComparison.OrdinalIgnoreCase)) return false;
        Ask(owner, $"This page's link was made with the GitHub account \"{share.Owner}\", but you are signed in as \"{user}\".\n\nDisconnect in Settings › Pages, sign in as \"{share.Owner}\" and try again.",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    /// <summary>Makes the page's public link, or updates it so the same link shows the page as it is now.</summary>
    public static async Task Link(Window? owner, string page)
    {
        try
        {
            if (await Login(owner, page, offerCopy: true) is not var (token, user)) return;
            var share = Shares.Get(page);
            if (share is not null && OtherAccount(owner, share, user)) return;

            var source = await File.ReadAllTextAsync(page);
            var findings = await Task.Run(() => SecretScan.Find(source));
            var warn = findings.Count == 0 ? "" :
                "\n\nThis page may hold things that shouldn't leave this PC:\n" +
                string.Join("\n", findings.Take(8).Select(f => $"  line {f.Line}: {f.What}")) +
                (findings.Count > 8 ? $"\n  and {findings.Count - 8} more" : "");
            var history = share is null ? "" :
                "\n\nThe link keeps this page's earlier versions in its history, where anyone with the link can read them. To drop them, remove the link and make a new one.";
            if (Ask(owner, $"Anyone with the link can view this page. It goes in a secret gist on \"{user}\"'s GitHub: not listed, but not private. Pictures in the page aren't checked.{warn}{history}\n\n{(warn.Length > 0 ? "Share anyway?" : share is null ? "Create the link?" : "Update the link?")}",
                    MessageBoxButton.OKCancel, warn.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;

            var html = await PageBundle.Make(source, PageTheme.CurrentCss(),
                Path.Combine(AppContext.BaseDirectory, "Assets", "pages"), PageBundle.FetchCached);
            var file = Path.GetFileName(page);
            // Small enough for gistpreview, which shows changes within a minute or two; else githack.
            var live = GitHubGist.FitsLive(html);
            bool updated = false;
            if (share is not null)
            {
                // Same account (checked above), so "not found" means it was deleted on GitHub.
                try { await GitHubGist.Update(token, share.GistId, share.File, html); updated = true; }
                catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound) { share = null; }
            }
            if (share is null)
            {
                string id, gistOwner;
                // An account GitHub won't let make gists (some company-managed ones).
                try { (id, gistOwner) = await GitHubGist.Create(token, file, html); }
                catch (GitHubException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { NoGist(owner); return; }
                share = new PageShare(id, gistOwner, file, live);
                Shares.Set(page, share);
            }
            // A page that grew past the limit moves to githack, and back; an old share moves on its first update.
            else if (share.Live != live) Shares.Set(page, share = share with { Live = live });

            var copied = Copy(share.Url);
            var note = live ? "" : updated
                ? "The gist now holds this version, but the link can keep showing the old one for a while: the preview service (githack) keeps its own copy. "
                : "Pages with diagrams use a link that can take a while to update or go away. ";
            if (Ask(owner, $"{(copied ? "Link copied" : "Link")}:\n{share.Url}\n\n{note}Open it?",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(share.Url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (Ask(owner, "Couldn't reach GitHub. Save a copy instead?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await TerminalPane.SaveCopy(owner, page);
        }
        catch (Exception e) when (Failed(owner, e)) { }
    }

    /// <summary>
    /// Deletes the page's gist. True when it is gone. A live link stops within a minute or two;
    /// githack keeps serving its copy for a while after (measured: 20+ minutes), and the
    /// confirm says so.
    /// </summary>
    public static async Task<bool> Unshare(Window? owner, string page, bool ask)
    {
        if (Shares.Get(page) is not { } share) return true;
        var after = share.Live ? "The link stops working within a minute or two."
            : "The link itself can keep working for a while: the preview service (githack) keeps its own copy.";
        if (ask && Ask(owner, $"Remove the public link? Clayo deletes the gist on GitHub. {after}\n\n{share.Url}",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return false;
        try
        {
            if (await Login(owner, page, offerCopy: false) is not var (token, user) || OtherAccount(owner, share, user)) return false;
            await GitHubGist.Delete(token, share.GistId);
            Shares.Set(page, null);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Ask(owner, "Couldn't reach GitHub, so the link still works. Try again later.", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception e) when (Failed(owner, e)) { }
        return false;
    }

    // What else can go wrong talking to GitHub, said in a box rather than closing Clayo: a
    // refusal, an answer of another shape, a page that can't be read.
    private static bool Failed(Window? owner, Exception e)
    {
        var text = e switch
        {
            GitHubException { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } g =>
                $"GitHub didn't accept the login ({g.Message}). Disconnect in Settings › Pages and share again to sign in. The page is not lost.",
            GitHubException g => $"GitHub said: {g.Message}. The page is not lost.",
            IOException or UnauthorizedAccessException => $"Couldn't read the page: {e.Message}",
            InvalidCastException or InvalidOperationException or NullReferenceException or System.Text.Json.JsonException =>
                "GitHub's answer wasn't what Clayo expected. The page is not lost; try again later.",
            _ => null
        };
        if (text is null) return false;
        Ask(owner, text, MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }
}
