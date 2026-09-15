//

using System;
using System.Collections.Generic;
using System.Linq;
using TarnishedTool.Interfaces;
using TarnishedTool.Models;
using TarnishedTool.Utilities;

namespace TarnishedTool.Control;

/// <summary>
/// Things the source asked to be told about.
///
/// Every other operation changes the game. These change nothing: they ask
/// this build to say when something happens in it, so that a run can
/// settle an objective on the game's word rather than on the player's.
///
/// What can be seen is what the game raises an event flag for. Every boss
/// death sets one, and the items the game files as events set one; the
/// tool already ships both tables, by name, because it can revive a boss
/// and spawn an item. An ordinary enemy raises nothing at all, so "kill
/// three of anything" cannot be watched and is not offered.
///
/// A watch is put on when an objective is drawn and taken off when the
/// scene closes, through the same revert the effects use. Nothing here
/// knows what a scene or an objective is; it holds a list and says when
/// one of them fires.
/// </summary>
public sealed class Watches
{
    private readonly IEventLogReader _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private bool _reading;
    private int _next;

    private sealed class Watch
    {
        /// <summary>Flags that fire it, and what to call the thing when one does.</summary>
        public Dictionary<int, string> Named;
        /// <summary>What to say it was, where the flag itself does not name anything.</summary>
        public string Fallback;
    }

    /// <summary>Something a watch was waiting for happened; the text names it.</summary>
    public event Action<string> Fired;

    public Watches(IEventLogReader log) => _log = log;

    private readonly Lazy<List<BossRevive>> _bosses =
        new(() => DataLoader.GetBossRevives().SelectMany(a => a.Value).ToList());

    private readonly Lazy<Dictionary<string, List<Grace>>> _graces = new(DataLoader.GetGraces);

    /// <summary>
    /// The categories a source may name, and the resource each is held in.
    ///
    /// The names on the left are what a profile is written against, so
    /// they are the game's words rather than a file name. A category whose
    /// table turns out to carry no event ids is refused when it is asked
    /// for, rather than accepted and then silent.
    /// </summary>
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Key Items"] = "KeyItems",
        ["Cookbooks"] = "Cookbooks",
        ["Bell Bearings"] = "BellBearings",
        ["Crystal Tears"] = "CrystalTears",
        ["Ashes of War"] = "AoW",
        ["Sorceries"] = "Sorceries",
        ["Incantations"] = "Incantations",
        ["Talismans"] = "Talismans",
    };

    /// <summary>Watch every boss, or one by name. Returns the token that takes it off again.</summary>
    public string Boss(string name, string area)
    {
        var all = _bosses.Value;
        var wanted = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(name))
        {
            wanted = wanted.Where(b => string.Equals(b.BossName, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(area))
                wanted = wanted.Where(b => string.Equals(b.Area, area.Trim(), StringComparison.OrdinalIgnoreCase));
            var found = wanted.ToList();
            if (found.Count == 0)
                throw new OperationRefused("no boss called " + name.Trim() + (string.IsNullOrWhiteSpace(area) ? string.Empty : " in " + area.Trim()));
            wanted = found;
        }

        var flags = new Dictionary<int, string>();
        foreach (var boss in wanted)
        {
            if (boss.BossFlags == null || boss.BossFlags.Count == 0) continue;
            // The first of the boss flags, which is the one that means
            // dead. `SetValue` is what a revive writes, so it is false
            // on every death flag in the table and filtering for true
            // selected the handful of flags a revive *sets* instead --
            // four of them, across three bosses, leaving every other
            // boss with nothing to watch. `GetBossStatus` reads
            // `BossFlags[0]` to decide whether a boss is dead; so does
            // this.
            //
            // Only the first. The rest are flags a revive clears
            // alongside it -- a grace, a map marker -- and a watch that
            // fired on those would announce a death that had not
            // happened. The first-encounter flags are excluded for the
            // same reason: saying "settled" when somebody walked into
            // the room would be worse than silence.
            var dead = boss.BossFlags[0];
            flags[dead.EventId] = string.IsNullOrWhiteSpace(boss.Area) ? boss.BossName : boss.BossName + ", " + boss.Area;
        }
        if (flags.Count == 0) throw new OperationRefused("this build knows no death flags for that");
        return Add(new Watch { Named = flags, Fallback = "a boss" });
    }

    /// <summary>Watch a whole kind of item, or one by name.</summary>
    public string Item(string category, string name)
    {
        var named = !string.IsNullOrWhiteSpace(name);
        if (!named && string.IsNullOrWhiteSpace(category)) throw new OperationRefused("watch.item wants a category or a name");

        var lists = new List<(string Category, List<EventItem> Items)>();
        if (string.IsNullOrWhiteSpace(category))
            foreach (var pair in Categories) lists.Add((pair.Key, DataLoader.GetEventItems(pair.Value, pair.Key)));
        else
        {
            if (!Categories.TryGetValue(category.Trim(), out var resource))
                throw new OperationRefused("this build has no list called " + category.Trim());
            lists.Add((category.Trim(), DataLoader.GetEventItems(resource, category.Trim())));
        }

        var flags = new Dictionary<int, string>();
        foreach (var (which, items) in lists)
            foreach (var item in items)
            {
                if (item.EventId == 0) continue;
                if (named && !string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                flags[item.EventId] = item.Name;
            }

        if (flags.Count == 0)
            throw new OperationRefused(named
                ? "nothing called " + name.Trim() + " that the game raises a flag for"
                : "this build has no flags for " + category.Trim() + ", so nothing there can be seen");
        return Add(new Watch { Named = flags, Fallback = named ? name.Trim() : category?.Trim() ?? "an item" });
    }

    /// <summary>Watch for a grace being lit for the first time on this save.</summary>
    public string Grace(string name, string area)
    {
        var all = _graces.Value.SelectMany(a => a.Value).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(name))
        {
            all = all.Where(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(area))
                all = all.Where(g => string.Equals(g.MainArea, area.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var flags = new Dictionary<int, string>();
        foreach (var grace in all)
        {
            if (grace.FlagId == 0) continue;
            flags[grace.FlagId] = string.IsNullOrWhiteSpace(grace.MainArea) ? grace.Name : grace.Name + ", " + grace.MainArea;
        }
        if (flags.Count == 0) throw new OperationRefused("no grace this build knows a flag for");
        return Add(new Watch { Named = flags, Fallback = "a grace" });
    }

    /// <summary>Take one off. Unknown tokens are not an error: a revert may arrive twice.</summary>
    public void Stop(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_gate)
        {
            if (!_watches.Remove(token)) return;
            if (_watches.Count == 0) Reading(false);
        }
    }

    /// <summary>Everything, for a disconnect or a run ending.</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            _watches.Clear();
            Reading(false);
        }
    }

    private string Add(Watch watch)
    {
        lock (_gate)
        {
            var token = "w" + (++_next);
            _watches[token] = watch;
            Reading(true);
            return token;
        }
    }

    /// <summary>Held under `_gate`.</summary>
    private void Reading(bool wanted)
    {
        if (wanted == _reading) return;
        _reading = wanted;
        if (wanted)
        {
            _log.EntriesReceived += OnEntries;
            _log.Start();
        }
        else
        {
            _log.EntriesReceived -= OnEntries;
            _log.Stop();
        }
    }

    private void OnEntries(List<EventLogEntry> entries)
    {
        if (entries == null || entries.Count == 0) return;
        var fired = new List<string>();
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                // Only a flag going true. They flap both ways, and a flag
                // going false is a boss being revived or a save being
                // loaded, neither of which anybody settled.
                if (!entry.Value) continue;
                var id = unchecked((int)entry.EventId);
                foreach (var pair in _watches.ToList())
                {
                    if (!pair.Value.Named.TryGetValue(id, out var what)) continue;
                    // One firing is the end of that watch: an objective is
                    // settled once, and the run takes it off in its own
                    // time anyway when the scene closes.
                    _watches.Remove(pair.Key);
                    fired.Add(string.IsNullOrWhiteSpace(what) ? pair.Value.Fallback : what);
                }
            }
            if (_watches.Count == 0) Reading(false);
        }
        foreach (var what in fired) Fired?.Invoke(what);
    }
}
