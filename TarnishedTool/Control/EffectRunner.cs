//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace TarnishedTool.Control;

/// <summary>An effect that is currently applied, and how to take it back.</summary>
public sealed class LiveEffect
{
    public string Id { get; set; }
    public string Label { get; set; }
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
        public DateTime Until;
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

    public event Action<string> Logged;
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
                    Log("Could not put back " + step.Op + ": " + ex.Message);
                }
            }

            Log("Put back " + (string.IsNullOrEmpty(effect.Label) ? effect.Id : effect.Label) + " from a session that did not close cleanly.");
        }

        _log.Clear();
    }

    public void Apply(ApplyFrame frame)
    {
        foreach (var call in frame.Ops)
        {
            if (!_ops.Has(call.Op))
            {
                Answer(frame.Id, false, null, "this build has no " + call.Op);
                return;
            }

            if (!_consent.Allows(call.Op))
            {
                Answer(frame.Id, false, null, call.Op + " is switched off here");
                return;
            }
        }

        // A warp refuses rather than waits. Arriving somewhere unexpected
        // four minutes after the dice said so is worse than not arriving,
        // and it is the operation that can strand somebody.
        var impatient = frame.Ops.Any(o => o.Op.StartsWith("warp.", StringComparison.Ordinal));

        if (!_isReady())
        {
            if (impatient)
            {
                Answer(frame.Id, false, null, "the game is not ready to be moved");
                return;
            }

            _waiting.Add(new Waiting { Frame = frame, Until = DateTime.UtcNow + Patience });
            Log("Waiting for the game: " + Name(frame));
            Changed?.Invoke();
            return;
        }

        Run(frame);
    }

    private void Run(ApplyFrame frame)
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
        };

        foreach (var call in frame.Ops)
        {
            try
            {
                effect.Reverts.AddRange(_ops.Invoke(call.Op, new Args(call.Args)));
            }
            catch (Exception ex)
            {
                // Half an effect is not an effect. What landed comes back
                // off before anyone is told it failed.
                Undo(effect);
                var why = ex is OperationRefused ? ex.Message : call.Op + " failed: " + ex.Message;
                Log("Refused " + Name(frame) + ": " + why);
                Answer(frame.Id, false, null, why);
                return;
            }
        }

        _live.Add(effect);
        Save();
        Log("Applied " + effect.Label + (effect.Until.HasValue ? " for " + frame.For + "s" : string.Empty));
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

        Revert(id, quiet: false);
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
                Log("Could not take back " + step.Op + ": " + ex.Message);
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

        var stale = _waiting.Where(w => w.Until <= now).ToList();
        foreach (var w in stale)
        {
            _waiting.Remove(w);
            Log("Gave up waiting for the game: " + Name(w.Frame));
            Answer(w.Frame.Id, false, null, "the game was not ready in time");
        }

        if (!_isReady())
        {
            if (stale.Count > 0) Changed?.Invoke();
            return;
        }

        var ready = _waiting.ToList();
        _waiting.Clear();
        foreach (var w in ready) Run(w.Frame);
        Changed?.Invoke();
    }

    private void Save() => _log.Write(_live.Select(e => new StoredEffect
    {
        Id = e.Id,
        Label = e.Label,
        Steps = e.Reverts.Select(r => new StoredStep { Op = r.Op, Args = r.Args }).ToList(),
    }));

    private static string Name(ApplyFrame frame) => string.IsNullOrEmpty(frame.Label) ? frame.Id : frame.Label;

    private void Log(string line) => Logged?.Invoke(line);

    private void Answer(string id, bool ok, DateTime? until, string error) => Answered?.Invoke(id, ok, until, error);
}
