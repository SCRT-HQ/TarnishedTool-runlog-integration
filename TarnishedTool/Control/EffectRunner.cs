//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace TarnishedTool.Control;

/// <summary>An effect that is currently applied, and how to take it back.</summary>
public sealed class LiveEffect
{
    public string Id { get; set; }
    public string Label { get; set; }
    /// <summary>What it is filed under, where several come off together.</summary>
    public string Group { get; set; }
    /// <summary>When it comes off by itself, or null to hold until told.</summary>
    public DateTime? Until { get; set; }
    public List<RevertStep> Reverts { get; } = new();
}

/// <summary>
/// Applying is not the hard half. Reverting is.
///
/// An effect ends five ways: its time ran out, the source said to take it
/// off, the socket dropped, the game went away, or the tool is closing.
/// All five have to put back what the player had, which is why every
/// operation reports how to undo itself at the moment it is applied rather
/// than being flipped back later, and why the ledger is on disk.
///
/// Nothing is written into the game unless the game is ready for it. An
/// effect that arrives during a loading screen waits, with a deadline,
/// because memory written into a loading screen is written into memory
/// that is about to be replaced. An effect that has waited too long is
/// dropped and said so: a curse that lands four minutes late is worse than
/// one that never landed.
/// </summary>
public sealed class EffectRunner
{
    /// <summary>How long a waiting effect is worth still applying.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private readonly OperationRegistry _ops;
    private readonly Consent _consent;
    private readonly RestoreLog _log;
    private readonly Func<bool> _isReady;
    private readonly DispatcherTimer _timer;
    private readonly List<LiveEffect> _live = new();
    private readonly List<Waiting> _waiting = new();

    private sealed class Waiting
    {
        public ApplyFrame Frame;
        /// <summary>What was left of it once this build had its say; see Apply.</summary>
        public List<OpCall> Doable;
        /// <summary>
        /// When it stops being worth applying, or null where it never does.
        /// Only an effect that lasts a set time has a moment that passes;
        /// see Apply.
        /// </summary>
        public DateTime? Until;
    }

    public EffectRunner(OperationRegistry ops, Consent consent, RestoreLog log, Func<bool> isReady)
    {
        _ops = ops;
        _consent = consent;
        _log = log;
        _isReady = isReady;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>What is applied right now, for the tab to draw.</summary>
    public IReadOnlyList<LiveEffect> Live => _live;

    public int WaitingCount => _waiting.Count;

    public event Action<string, Chatter> Logged;
    /// <summary>id, whether it landed, when it comes off, and why not.</summary>
    public event Action<string, bool, DateTime?, string> Answered;
    public event Action Changed;

    /// <summary>
    /// Put back anything a previous run of this program left applied.
    /// Called once at startup, before anything connects.
    /// </summary>
    public void RestoreFromLastTime()
    {
        var left = _log.Read();
        if (left.Count == 0) return;
        foreach (var effect in left)
        {
            foreach (var step in Enumerable.Reverse(effect.Steps))
            {
                try
                {
                    _ops.Invoke(new RevertStep(step.Op, step.Args));
                }
                catch (Exception ex)
                {
                    Trouble("Could not put back " + step.Op + ": " + ex.Message);
                }
            }

            Log("Put back " + (string.IsNullOrEmpty(effect.Label) ? effect.Id : effect.Label) + " from a session that did not close cleanly.");
        }

        _log.Clear();
    }

    public void Apply(ApplyFrame frame)
    {
        // Every refusal is said twice: once back to whatever asked, and
        // once here. A person watching this tab do nothing needs to know
        // why, and the answer travelling back over the socket is not
        // somewhere they can see.
        // A frame that stands or falls together is checked together: one
        // operation this build has no name for, or one switched off, and
        // none of it happens. A frame of separate things loses only the
        // one, because eight gifts should not be cancelled by a ninth
        // written against a newer build than this.
        var doable = new List<OpCall>();
        foreach (var call in frame.Ops)
        {
            var no = !_ops.Has(call.Op) ? "this build has no " + call.Op
                : !_consent.Allows(call.Op) ? call.Op + " is switched off here"
                : null;
            if (no == null)
            {
                doable.Add(call);
                continue;
            }

            if (!frame.Each)
            {
                Refuse(frame, no);
                return;
            }

            Trouble("Left out of " + Name(frame) + ": " + no);
        }

        if (doable.Count == 0)
        {
            Refuse(frame, frame.Ops.Count == 0 ? "there is nothing in it" : "nothing in it can be done here");
            return;
        }

        // A warp refuses rather than waits. Arriving somewhere unexpected
        // four minutes after the dice said so is worse than not arriving,
        // and it is the operation that can strand somebody.
        var impatient = doable.Any(o => o.Op.StartsWith("warp.", StringComparison.Ordinal));

        if (!_isReady())
        {
            if (impatient)
            {
                Refuse(frame, "the game is not ready to be moved");
                return;
            }

            // A deadline is for an effect whose moment passes. Something
            // that lasts ninety seconds is worth nothing four minutes
            // later, so it is given up on. Something with no lifetime is
            // held until the source says otherwise -- a run's terms, a
            // curse that lasts a unit -- and its moment does not pass
            // while the source still means it to be in force.
            //
            // Waiting is the ordinary case here, not the exception: the
            // usual way to start is to attach, then launch the game and
            // load a save, which is minutes. Terms given a deadline were
            // dropped every time, and applied only when the tool connected
            // after a save was already in, which is backwards.
            var until = frame.For.HasValue ? DateTime.UtcNow + Patience : (DateTime?)null;
            _waiting.Add(new Waiting { Frame = frame, Doable = doable, Until = until });
            Log(until.HasValue ? "Waiting for the game: " + Name(frame) : "Waiting for the game, for as long as it takes: " + Name(frame));
            Changed?.Invoke();
            return;
        }

        Run(frame, doable);
    }

    private void Run(ApplyFrame frame, List<OpCall> doable)
    {
        // Already applied under this id: the source re-sent, or a
        // reconnection replayed it. Taking the first one off before
        // applying again keeps the ledger honest about what is in force.
        Revert(frame.Id, quiet: true);

        var effect = new LiveEffect
        {
            Id = frame.Id,
            Label = string.IsNullOrEmpty(frame.Label) ? frame.Id : frame.Label,
            Until = frame.For.HasValue && frame.For.Value > 0 ? DateTime.UtcNow.AddSeconds(frame.For.Value) : (DateTime?)null,
            Group = frame.Group,
        };

        var lost = 0;
        foreach (var call in doable)
        {
            try
            {
                Detail("  " + call.Op + " " + Describe(call.Args));
                effect.Reverts.AddRange(_ops.Invoke(call.Op, new Args(call.Args)));
            }
            catch (Exception ex)
            {
                var why = ex is OperationRefused ? ex.Message : call.Op + " failed: " + ex.Message;

                // Half an effect is not an effect. What landed comes back
                // off before anyone is told it failed.
                if (!frame.Each)
                {
                    Undo(effect);
                    Refuse(frame, why);
                    return;
                }

                // A list of separate things loses the one that failed and
                // goes on. Said out loud, because a gift that quietly did
                // not arrive is worse than one that says why.
                lost++;
                Trouble("Left out of " + Name(frame) + ": " + why);
            }
        }

        if (effect.Reverts.Count == 0 && lost > 0)
        {
            Refuse(frame, "none of it could be done");
            return;
        }

        _live.Add(effect);
        Save();
        Log("Applied " + effect.Label + (lost > 0 ? " without " + lost + " of it" : string.Empty) + (effect.Until.HasValue ? " for " + frame.For + "s" : string.Empty));
        Answer(effect.Id, true, effect.Until, null);
        Changed?.Invoke();
    }

    /// <summary>The id that means all of them, whatever they were.</summary>
    public const string Everything = "*";

    public void Revert(string id)
    {
        // A run that has ended says this rather than naming every effect
        // it applied, since it is the tool that knows what is in force.
        if (string.Equals(id, Everything, StringComparison.Ordinal))
        {
            RevertAll("the source asked");
            return;
        }

        // A name ending in the same character takes back everything
        // filed under it. One result can match several rules and so
        // apply several effects, each under its own number, and a
        // source taking that result back knows the number and not how
        // many rules it happened to match.
        if (id != null && id.EndsWith(Everything, StringComparison.Ordinal))
        {
            var prefix = id.Substring(0, id.Length - Everything.Length);
            var going = _live.Where(e => e.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var effect in Enumerable.Reverse(going)) Undo(effect);
            foreach (var effect in going) _live.Remove(effect);
            var dropped = _waiting.Where(w => w.Frame.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var w in dropped) _waiting.Remove(w);
            if (going.Count > 0 || dropped.Count > 0)
            {
                Save();
                Log("Took back " + (going.Count + dropped.Count) + (going.Count + dropped.Count == 1 ? " effect" : " effects") + " from " + prefix);
                Changed?.Invoke();
            }

            return;
        }

        Revert(id, quiet: false);
    }

    /// <summary>
    /// Everything filed under one group, off together.
    ///
    /// Which is how an effect that lasts a unit of play ends: the source
    /// says when the unit closed, and the tool knows what it applied
    /// while that unit was open.
    /// </summary>
    public void RevertGroup(string group)
    {
        if (string.IsNullOrEmpty(group)) return;
        var going = _live.Where(e => string.Equals(e.Group, group, StringComparison.Ordinal)).ToList();
        var waiting = _waiting.Where(w => string.Equals(w.Frame.Group, group, StringComparison.Ordinal)).ToList();
        foreach (var w in waiting) _waiting.Remove(w);
        if (going.Count == 0)
        {
            if (waiting.Count > 0) Changed?.Invoke();
            return;
        }

        foreach (var effect in Enumerable.Reverse(going)) Undo(effect);
        foreach (var effect in going) _live.Remove(effect);
        Save();
        Log("Took back " + going.Count + (going.Count == 1 ? " effect" : " effects") + " from " + group);
        Changed?.Invoke();
    }

    private void Revert(string id, bool quiet)
    {
        var effect = _live.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        var waiting = _waiting.Where(w => string.Equals(w.Frame.Id, id, StringComparison.Ordinal)).ToList();
        foreach (var w in waiting) _waiting.Remove(w);
        if (effect == null)
        {
            if (!quiet && waiting.Count > 0) Log("Dropped " + id + " before it landed.");
            if (waiting.Count > 0) Changed?.Invoke();
            return;
        }

        Undo(effect);
        _live.Remove(effect);
        Save();
        if (!quiet) Log("Took back " + effect.Label);
        Changed?.Invoke();
    }

    /// <summary>
    /// Everything off, in reverse order. The socket dropped, the game went
    /// away, the run ended, or the tool is closing.
    /// </summary>
    public void RevertAll(string why)
    {
        // Said on every detach, and the window publishes that one many
        // times a second while no game is running, so nothing at all
        // happens when there is nothing to take back.
        var wasWaiting = _waiting.Count;
        _waiting.Clear();
        if (_live.Count == 0)
        {
            if (wasWaiting > 0) Changed?.Invoke();
            return;
        }

        foreach (var effect in Enumerable.Reverse(_live).ToList()) Undo(effect);
        Log("Took back " + _live.Count + (_live.Count == 1 ? " effect: " : " effects: ") + why);
        _live.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Undo(LiveEffect effect)
    {
        foreach (var step in Enumerable.Reverse(effect.Reverts))
        {
            try
            {
                _ops.Invoke(step);
            }
            catch (Exception ex)
            {
                Trouble("Could not take back " + step.Op + ": " + ex.Message);
            }
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;

        var expired = _live.Where(e => e.Until.HasValue && e.Until.Value <= now).ToList();
        foreach (var effect in expired)
        {
            Undo(effect);
            _live.Remove(effect);
            Log(effect.Label + " ran out.");
        }

        if (expired.Count > 0)
        {
            Save();
            Changed?.Invoke();
        }

        if (_waiting.Count == 0) return;

        var stale = _waiting.Where(w => w.Until.HasValue && w.Until.Value <= now).ToList();
        foreach (var w in stale)
        {
            _waiting.Remove(w);
            Trouble("Gave up waiting for the game: " + Name(w.Frame));
            Answer(w.Frame.Id, false, null, "the game was not ready in time");
        }

        if (!_isReady())
        {
            if (stale.Count > 0) Changed?.Invoke();
            return;
        }

        var ready = _waiting.ToList();
        _waiting.Clear();
        foreach (var w in ready) Run(w.Frame, w.Doable);
        Changed?.Invoke();
    }

    private void Save() => _log.Write(_live.Select(e => new StoredEffect
    {
        Id = e.Id,
        Label = e.Label,
        Group = e.Group,
        Steps = e.Reverts.Select(r => new StoredStep { Op = r.Op, Args = r.Args }).ToList(),
    }));

    /// <summary>Told to whoever asked, and to whoever is watching this tab.</summary>
    private void Refuse(ApplyFrame frame, string why)
    {
        Trouble("Refused " + Name(frame) + ": " + why);
        Answer(frame.Id, false, null, why);
    }

    private static string Name(ApplyFrame frame) => string.IsNullOrEmpty(frame.Label) ? frame.Id : frame.Label;

    /// <summary>An operation's arguments, for reading rather than parsing.</summary>
    private static string Describe(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "(nothing)";
        var parts = args.EnumerateObject().Select(a => a.Name + "=" + a.Value.ToString()).ToList();
        return parts.Count == 0 ? "(nothing)" : string.Join(", ", parts);
    }

    private void Log(string line) => Logged?.Invoke(line, Chatter.Normal);

    /// <summary>A line only somebody setting a profile up wants to read.</summary>
    private void Detail(string line) => Logged?.Invoke(line, Chatter.Everything);

    /// <summary>A line worth reading however little anybody asked for.</summary>
    private void Trouble(string line) => Logged?.Invoke(line, Chatter.Quiet);

    private void Answer(string id, bool ok, DateTime? until, string error) => Answered?.Invoke(id, ok, until, error);
}
