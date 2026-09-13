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
/// The disruptive ones start off: a first connection can change settings
/// and apply effects, and cannot warp anyone, hand out items or press a
/// button, until this player says so.
/// </summary>
public sealed class Consent
{
    private static readonly string[] OffUnlessAsked = { "warp.position", "warp.grace", "player.drop", "item.give", "action.invoke" };

    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);

    public event Action Changed;

    public static IEnumerable<string> DefaultFor(IEnumerable<string> ops) =>
        ops.Where(op => !OffUnlessAsked.Contains(op, StringComparer.Ordinal));

    public void Load(string saved, IEnumerable<string> allOps)
    {
        _allowed.Clear();
        if (string.IsNullOrWhiteSpace(saved))
        {
            foreach (var op in DefaultFor(allOps)) _allowed.Add(op);
            return;
        }

        // Somebody who switched everything off meant it, and must not
        // find it all switched on again tomorrow.
        if (saved.Trim() == Nothing) return;

        foreach (var op in saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            _allowed.Add(op.Trim());
    }

    /// <summary>Written where nothing is allowed, so that reads back as itself.</summary>
    private const string Nothing = "-";

    public string Saved => _allowed.Count == 0 ? Nothing : string.Join(",", _allowed.OrderBy(o => o, StringComparer.Ordinal));

    public bool Allows(string op) => _allowed.Contains(op);

    public void Set(string op, bool allowed)
    {
        var changed = allowed ? _allowed.Add(op) : _allowed.Remove(op);
        if (changed) Changed?.Invoke();
    }
}
