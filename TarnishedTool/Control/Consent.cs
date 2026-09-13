//

using System;
using System.Collections.Generic;
using System.Linq;

namespace TarnishedTool.Control;

/// <summary>
/// What this player has agreed a source may do.
///
/// Being moved across the map by somebody else's dice is fun by consent
/// and unpleasant without it, so the list is here, in front of the person
/// it happens to, rather than being a property of whatever is driving.
/// Everything starts on, and the list is how somebody turns a thing off:
/// this only ever reaches a game that is already offline and already
/// being driven on purpose, and a first connection that silently does
/// half of what a profile says is worse than one that does all of it.
///
/// What is kept is therefore what was switched *off*, not what was left
/// on. The difference shows the first time a build learns a new
/// operation: kept the other way round, every operation added after
/// somebody last touched this list arrives switched off, with nothing
/// saying so until a profile is refused by name.
/// </summary>
public sealed class Consent
{
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
    private readonly List<string> _all = new();

    public event Action Changed;

    public void Load(string saved, IEnumerable<string> allOps)
    {
        _allowed.Clear();
        _all.Clear();
        _all.AddRange(allOps);

        // Never touched: all of it.
        if (string.IsNullOrWhiteSpace(saved))
        {
            foreach (var op in _all) _allowed.Add(op);
            return;
        }

        // Somebody who switched everything off meant it, and must not
        // find it all switched on again tomorrow.
        if (saved.Trim() == Nothing) return;

        if (saved.StartsWith(Denied, StringComparison.Ordinal))
        {
            var off = new HashSet<string>(
                saved.Substring(Denied.Length).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(o => o.Trim()),
                StringComparer.Ordinal);
            foreach (var op in _all)
                if (!off.Contains(op))
                    _allowed.Add(op);
            return;
        }

        // Written by a build that kept the other list. Read as it was
        // meant, so nobody's choices turn themselves back on; it is
        // written the new way the next time they change one.
        foreach (var op in saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            _allowed.Add(op.Trim());
    }

    /// <summary>Written where nothing is allowed, so that reads back as itself.</summary>
    private const string Nothing = "-";

    /// <summary>What follows is the list of operations switched off.</summary>
    private const string Denied = "!";

    public string Saved =>
        _allowed.Count == 0
            ? Nothing
            : Denied + string.Join(",", _all.Where(op => !_allowed.Contains(op)).OrderBy(o => o, StringComparer.Ordinal));

    public bool Allows(string op) => _allowed.Contains(op);

    public void Set(string op, bool allowed)
    {
        var changed = allowed ? _allowed.Add(op) : _allowed.Remove(op);
        if (changed) Changed?.Invoke();
    }
}
