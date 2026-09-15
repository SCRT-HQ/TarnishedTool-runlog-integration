//

using System;
using TarnishedTool.Interfaces;

namespace TarnishedTool.Control;

/// <summary>
/// Watching the game for the few things worth telling a source about.
///
/// The other direction: everything else in this folder is about a source
/// moving the game, and this is the game saying what happened to it. It
/// exists because a host otherwise sits there typing "I died" into a
/// browser with one hand while playing with the other, which is the sort
/// of bookkeeping a program should be doing.
///
/// Deliberately shallow. It reads what the tool already reads, on the
/// tick the tool already runs, and says one thing: you died. Anything
/// cleverer wants to be sure, and this cannot be sure of much.
/// </summary>
public sealed class DeathWatcher
{
    private readonly IGameTickService _tick;
    private readonly IPlayerService _player;
    private readonly Func<bool> _isReady;

    /// <summary>
    /// Whether the player was last seen alive.
    ///
    /// A death is the edge between alive and not, never the state of
    /// being at zero: health reads zero in plenty of moments that are not
    /// a death, a loading screen among them, and a watcher that counted
    /// those would report a dozen deaths for one.
    /// </summary>
    private bool _wasAlive;

    private bool _watching;

    public DeathWatcher(IGameTickService tick, IPlayerService player, Func<bool> isReady)
    {
        _tick = tick;
        _player = player;
        _isReady = isReady;
    }

    public event Action Died;

    public bool IsWatching => _watching;

    public void Start()
    {
        if (_watching) return;
        _wasAlive = false;
        _tick.Subscribe(OnTick);
        _watching = true;
    }

    public void Stop()
    {
        if (!_watching) return;
        _tick.Unsubscribe(OnTick);
        _watching = false;
        _wasAlive = false;
    }

    private void OnTick()
    {
        // Between attaching and being properly in the world, everything
        // reads as something. Nothing counts until the game is ready, and
        // the next reading of a live player starts the watch again.
        if (!_isReady())
        {
            _wasAlive = false;
            return;
        }

        int hp;
        try
        {
            hp = _player.GetCurrentHp();
        }
        catch (Exception)
        {
            // A read that failed is not a death.
            _wasAlive = false;
            return;
        }

        if (hp > 0)
        {
            _wasAlive = true;
            return;
        }

        if (!_wasAlive) return;
        _wasAlive = false;
        Died?.Invoke();
    }
}
