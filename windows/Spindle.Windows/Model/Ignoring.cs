using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;

namespace Spindle.Windows.Model;

// Repositories the person ignores, as the Mac's SplitAdvisor: none of their prompts and runs show or are uploaded,
// their prompts only end the attention before them, and the list goes to the account as hashes alone.
public sealed partial class SplitAdvisor
{
    /// <summary>The ignore list the days read so far were read with.</summary>
    private IgnoredRepositories readWith = IgnoredRepositories.None;

    private IgnoredList Ignored => owner.Preferences.IgnoredRepositories;

    /// <summary>The person's account, whose ID keys the hashes; none before they signed in.</summary>
    private string? Account => owner.Me?.Id.ToString("D");

    /// <summary>The repositories to leave out of everything read from the logs. Another account's list counts for nothing.</summary>
    public IgnoredRepositories Ignoring =>
        Account is { } account ? new IgnoredRepositories(account, Ignored.Account == account ? Ignored.Hashes : []) : IgnoredRepositories.None;

    /// <summary>Whether the person ignores this repository.</summary>
    public bool IsIgnored(AgentRepository repository) => Ignoring.Contains(repository);

    /// <summary>The ignored repositories, by hash, with their names as this PC knew them (null for one ignored elsewhere and never seen here).</summary>
    public List<(string Hash, string? Name)> IgnoredList() =>
        Ignoring.Hashes.Select(h => (Hash: h, Name: Ignored.Names.GetValueOrDefault(h)))
            .OrderBy(i => i.Name is null).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Hash, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Ignores a repository on every device: none of its prompts and runs show or are uploaded from now on, and its
    /// link goes, so the account keeps nothing that names it. Returns its hash, to stop ignoring it with.
    /// </summary>
    public async Task<string?> IgnoreAsync(AgentRepository repository)
    {
        if (Account is not { } account) return null;
        var hash = IgnoredRepositories.Hash(repository.Identity, account);
        var list = Own(account);
        Save(list with { Hashes = list.Hashes.Adding(hash), Names = list.Names.With(hash, repository.Name), Unsent = list.Unsent.With(hash, true) });
        ForgetLink(repository.Identity);
        ReadAgain();
        await SendIgnoredAsync();
        return hash;
    }

    /// <summary>Stops ignoring a repository on every device; its activity comes back from the logs, which were never changed.</summary>
    public async Task UnignoreAsync(string hash)
    {
        if (Account is not { } account) return;
        var list = Own(account);
        Save(list with { Hashes = list.Hashes.Removing(hash), Unsent = list.Unsent.With(hash, false) });
        ReadAgain();
        await SendIgnoredAsync();
    }

    /// <summary>The list for this account: another account's is dropped.</summary>
    private IgnoredList Own(string account) => Ignored.Account == account ? Ignored : new IgnoredList { Account = account };

    private void Save(IgnoredList list) =>
        owner.SaveIgnoredRepositories(list with { Names = new(list.Names.Where(n => list.Hashes.Contains(n.Key) || list.Unsent.ContainsKey(n.Key))) });

    /// <summary>What was read had other repositories left out: it is read again.</summary>
    private void ReadAgain()
    {
        Revision++;
        ForgetActivity();
    }

    /// <summary>Reads with the current list, dropping the days read with another.</summary>
    private void ReadWithIgnoring()
    {
        var ignoring = Ignoring;
        if (ignoring.Equals(readWith)) return;
        readWith = ignoring;
        owner.Activity.Ignoring = ignoring;
        days.Clear();
        learned = null;
    }

    private void ForgetLink(string identity)
    {
        chosen.Remove(identity);
        rules.Remove(identity);
        noProject.Remove(identity);
        names.Remove(identity);
        unsent.Remove(identity);
    }

    /// <summary>Sends what was changed here, before links, so a link chosen after a repository was no longer ignored is kept.</summary>
    private async Task SendIgnoredAsync()
    {
        if (owner.Api is not { } api || Account is not { } account) return;
        foreach (var (hash, ignored) in Own(account).Unsent.ToList())
        {
            try
            {
                if (ignored) await api.IgnoreRepositoryAsync(hash);
                else await api.UnignoreRepositoryAsync(hash);
                var now = Own(account);
                if (now.Unsent.TryGetValue(hash, out var still) && still == ignored) Save(now with { Unsent = now.Unsent.Without(hash) });
            }
            catch (Exception)
            {
                // Sent again when the account's links are read next.
                return;
            }
        }
    }

    /// <summary>
    /// The account's list, with what was changed here and not sent yet. Names come from links and activity read so far,
    /// before both forget the repositories.
    /// </summary>
    private void AbsorbIgnored(IReadOnlyList<string> hashes)
    {
        if (Account is not { } account) return;
        var list = Own(account);
        var next = hashes.ToHashSet(StringComparer.Ordinal);
        foreach (var (hash, ignored) in list.Unsent)
        {
            if (ignored) next.Add(hash);
            else next.Remove(hash);
        }
        if (next.SetEquals(list.Hashes)) return;
        var added = next.Except(list.Hashes).ToHashSet(StringComparer.Ordinal);
        var named = list.Names;
        var known = names.Keys.Concat(days.Values.SelectMany(d => d.Snapshot.Attention).Select(a => a.Repository?.Identity).OfType<string>()).Distinct().ToList();
        foreach (var identity in known)
        {
            var hash = IgnoredRepositories.Hash(identity, account);
            if (!added.Contains(hash)) continue;
            if (!named.ContainsKey(hash)) named = named.With(hash, Repository(identity).Name);
            ForgetLink(identity);
        }
        Save(list with { Hashes = new KeptList(next.Order(StringComparer.Ordinal)), Names = named });
        ReadAgain();
    }
}
