//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TarnishedTool.Enums;
using TarnishedTool.Interfaces;
using TarnishedTool.Models;
using TarnishedTool.Utilities;
using TarnishedTool.ViewModels;

namespace TarnishedTool.Control;

/// <summary>
/// The operations, bound to the tool's own view models and services.
///
/// This is the one file in the control layer that knows what game this is.
/// Everything else moves names and arguments around; here a name becomes a
/// property somebody can also click.
///
/// Two rules it follows. Effects drive the view models rather than the
/// hotkey actions, because a hotkey action flips a switch and an effect has
/// to *set* one: flipping cannot be applied twice, cannot be put back to
/// what the player had, and quietly undoes a setting somebody turned on by
/// hand. And every setting reads its old value on the way in, so reverting
/// restores rather than clears.
/// </summary>
public sealed class GameOperations
{
    private readonly Dictionary<string, Toggle> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumberSetting> _values = new(StringComparer.Ordinal);
    private readonly ISpEffectService _spEffects;
    private readonly IPlayerService _player;
    private readonly ITravelService _travel;
    private readonly IItemService _items;
    private readonly HotkeyManager _hotkeys;
    /// <summary>
    /// Every grace the tool knows, by area and by name.
    ///
    /// This is why a source can name a destination without knowing a
    /// single coordinate. The tool already ships these and already
    /// updates them when the game patches; a run that says "Church of
    /// Elleh" is saying something that stays true, where three numbers
    /// would not.
    /// </summary>
    private readonly Lazy<List<Grace>> _graces = new(() => DataLoader.GetGraces().SelectMany(a => a.Value).ToList());

    private sealed class Toggle
    {
        public Func<bool> Read;
        public Action<bool> Write;
    }

    private sealed class NumberSetting
    {
        public Func<float> Read;
        public Action<float> Write;
        public float Least;
        public float Most;
    }

    public GameOperations(
        PlayerViewModel player,
        EnemyViewModel enemies,
        UtilityViewModel utility,
        TravelViewModel travel,
        ISpEffectService spEffects,
        IPlayerService playerService,
        ITravelService travelService,
        IItemService items,
        HotkeyManager hotkeys)
    {
        _spEffects = spEffects;
        _player = playerService;
        _travel = travelService;
        _items = items;
        _hotkeys = hotkeys;

        // What a run may switch on and off. The names are the tool's own
        // vocabulary rather than a pack's: a source maps its words to
        // these, which is what keeps a pack playable with nothing attached.
        Flag("player.noDeath", () => player.IsNoDeathEnabled, v => player.IsNoDeathEnabled = v);
        Flag("player.noDamage", () => player.IsNoDamageEnabled, v => player.IsNoDamageEnabled = v);
        Flag("player.noHit", () => player.IsNoHitEnabled, v => player.IsNoHitEnabled = v);
        Flag("player.oneShot", () => player.IsOneShotEnabled, v => player.IsOneShotEnabled = v);
        Flag("player.noRoll", () => player.IsNoRollEnabled, v => player.IsNoRollEnabled = v);
        Flag("player.infiniteStamina", () => player.IsInfiniteStaminaEnabled, v => player.IsInfiniteStaminaEnabled = v);
        Flag("player.infiniteFp", () => player.IsInfiniteFpEnabled, v => player.IsInfiniteFpEnabled = v);
        Flag("player.infiniteArrows", () => player.IsInfiniteArrowsEnabled, v => player.IsInfiniteArrowsEnabled = v);
        Flag("player.infiniteConsumables", () => player.IsInfiniteConsumablesEnabled, v => player.IsInfiniteConsumablesEnabled = v);
        Flag("player.infinitePoise", () => player.IsInfinitePoiseEnabled, v => player.IsInfinitePoiseEnabled = v);
        Flag("player.lockHp", () => player.IsHpLocked, v => player.IsHpLocked = v);
        Flag("player.healOverTime", () => player.IsHotEnabled, v => player.IsHotEnabled = v);
        Flag("player.fpRegen", () => player.IsFpRegenEnabled, v => player.IsFpRegenEnabled = v);
        Flag("player.silent", () => player.IsSilentEnabled, v => player.IsSilentEnabled = v);
        Flag("player.hidden", () => player.IsHiddenEnabled, v => player.IsHiddenEnabled = v);
        Flag("player.speedBuff", () => player.IsSpeedBuffEnabled, v => player.IsSpeedBuffEnabled = v);
        Flag("player.torrentNoDeath", () => player.IsTorrentNoDeathEnabled, v => player.IsTorrentNoDeathEnabled = v);
        Flag("player.torrentAnywhere", () => player.IsTorrentAnywhereEnabled, v => player.IsTorrentAnywhereEnabled = v);
        Flag("player.noRuneGain", () => player.IsNoRuneGainEnabled, v => player.IsNoRuneGainEnabled = v);
        Flag("player.noRuneLoss", () => player.IsNoRuneLossEnabled, v => player.IsNoRuneLossEnabled = v);

        Flag("enemies.noDeath", () => enemies.IsNoDeathEnabled, v => enemies.IsNoDeathEnabled = v);
        Flag("enemies.noDamage", () => enemies.IsNoDamageEnabled, v => enemies.IsNoDamageEnabled = v);
        Flag("enemies.noAttack", () => enemies.IsNoAttackEnabled, v => enemies.IsNoAttackEnabled = v);
        Flag("enemies.noMove", () => enemies.IsNoMoveEnabled, v => enemies.IsNoMoveEnabled = v);
        Flag("enemies.noAi", () => enemies.IsDisableAiEnabled, v => enemies.IsDisableAiEnabled = v);

        Flag("world.freeze", () => utility.IsFreezeWorldEnabled, v => utility.IsFreezeWorldEnabled = v);
        Flag("world.noCutscenes", () => utility.IsDisableCutscenesEnabled, v => utility.IsDisableCutscenesEnabled = v);
        Flag("world.guaranteedDrop", () => utility.IsGuaranteedDropEnabled, v => utility.IsGuaranteedDropEnabled = v);
        Flag("world.mapInCombat", () => utility.IsCombatMapEnabled, v => utility.IsCombatMapEnabled = v);
        Flag("world.warpInDungeons", () => utility.IsDungeonWarpEnabled, v => utility.IsDungeonWarpEnabled = v);
        Flag("world.hideCharacters", () => utility.IsHideCharactersEnabled, v => utility.IsHideCharactersEnabled = v);
        Flag("world.hideMap", () => utility.IsHideMapEnabled, v => utility.IsHideMapEnabled = v);
        Flag("travel.restOnWarp", () => travel.IsRestOnWarpEnabled, v => travel.IsRestOnWarpEnabled = v);
        Flag("travel.showAllGraces", () => travel.IsShowAllGracesEnabled, v => travel.IsShowAllGracesEnabled = v);

        // Bounds are this file's business rather than the source's. A run
        // that asks for a speed of forty is a mapping somebody typed wrong,
        // and the answer is to refuse it rather than to make it unplayable.
        Number("player.speed", () => player.PlayerSpeed, v => player.PlayerSpeed = v, 0.1f, 10f);
        Number("game.speed", () => utility.GameSpeed, v => utility.GameSpeed = v, 0.1f, 10f);
        Number("game.fps", () => utility.Fps, v => utility.Fps = (int)v, 20f, 240f);
        Number("player.runes", () => player.Runes, v => player.Runes = (int)v, 0f, 999999999f);
        Number("player.newGame", () => player.NewGame, v => player.NewGame = (int)v, 0f, 7f);
        Number("player.vigor", () => player.Vigor, v => player.Vigor = (int)v, 1f, 99f);
        Number("player.mind", () => player.Mind, v => player.Mind = (int)v, 1f, 99f);
        Number("player.endurance", () => player.Endurance, v => player.Endurance = (int)v, 1f, 99f);
        Number("player.strength", () => player.Strength, v => player.Strength = (int)v, 1f, 99f);
        Number("player.dexterity", () => player.Dexterity, v => player.Dexterity = (int)v, 1f, 99f);
        Number("player.intelligence", () => player.Intelligence, v => player.Intelligence = (int)v, 1f, 99f);
        Number("player.faith", () => player.Faith, v => player.Faith = (int)v, 1f, 99f);
        Number("player.arcane", () => player.Arcane, v => player.Arcane = (int)v, 1f, 99f);
        Number("player.incomingDamage", () => player.IncomingDamageMultiplier, v => player.IncomingDamageMultiplier = v, 0f, 100f);
        Number("player.outgoingDamage", () => player.OutgoingDamageMultiplier, v => player.OutgoingDamageMultiplier = v, 0f, 100f);
    }

    /// <summary>The names a source may switch, for the panel to list.</summary>
    public IEnumerable<string> FlagNames => _flags.Keys.OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>The names a source may set, for the panel to list.</summary>
    public IEnumerable<string> ValueNames => _values.Keys.OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// One-shot actions a source may press by name.
    ///
    /// Deliberately short. The tool has over two hundred registered
    /// actions and exposing the lot would hand a website a switch on every
    /// feature in it; these are the ones a game about dice actually wants,
    /// and every one of them is something the player could not have been
    /// in the middle of undoing.
    /// </summary>
    private static readonly HotkeyActions[] Pressable =
    {
        HotkeyActions.Quitout,
        HotkeyActions.ForceSave,
        HotkeyActions.Rest,
        HotkeyActions.RuneArc,
        HotkeyActions.SetMaxHp,
        HotkeyActions.SetRfbs,
        HotkeyActions.KillTarget,
        HotkeyActions.SetMorning,
        HotkeyActions.SetNoon,
        HotkeyActions.SetDusk,
        HotkeyActions.SetNight,
        HotkeyActions.DefaultWeather,
        HotkeyActions.RainyWeather,
        HotkeyActions.SnowyWeather,
        HotkeyActions.FoggyWeather,
    };

    public void RegisterOn(OperationRegistry registry)
    {
        registry.Register("flag.set", args =>
        {
            var name = args.Text("name");
            var value = args.Flag("value");
            if (name == null || value == null) throw new OperationRefused("flag.set wants a name and a value");
            if (!_flags.TryGetValue(name, out var flag)) throw new OperationRefused("no such flag: " + name);
            var was = flag.Read();
            if (was == value.Value) return Array.Empty<RevertStep>();
            flag.Write(value.Value);
            return new[] { RevertStep.Of("flag.set", "name", name, "value", was) };
        });

        registry.Register("value.set", args =>
        {
            var name = args.Text("name");
            var value = args.Real("value");
            if (name == null || value == null) throw new OperationRefused("value.set wants a name and a value");
            if (!_values.TryGetValue(name, out var number)) throw new OperationRefused("no such value: " + name);
            if (value.Value < number.Least || value.Value > number.Most)
                throw new OperationRefused(name + " takes " + number.Least + " to " + number.Most);
            var was = number.Read();
            number.Write(value.Value);
            return new[] { RevertStep.Of("value.set", "name", name, "value", was) };
        });

        registry.Register("speffect.apply", args =>
        {
            var id = args.Unsigned("id");
            if (id == null) throw new OperationRefused("speffect.apply wants an id");
            var who = _player.GetPlayerIns();
            if (who == 0) throw new OperationRefused("there is no player to apply it to");
            _spEffects.ApplySpEffect(who, id.Value);
            return new[] { RevertStep.Of("speffect.remove", "id", id.Value) };
        });

        registry.RegisterOneShot("speffect.remove", args =>
        {
            var id = args.Unsigned("id");
            if (id == null) throw new OperationRefused("speffect.remove wants an id");
            var who = _player.GetPlayerIns();
            if (who == 0) throw new OperationRefused("there is no player to take it off");
            _spEffects.RemoveSpEffect(who, id.Value);
        });

        // A warp is the most disruptive thing on this list and the one that
        // can cost somebody an evening, so it is the one that refuses
        // rather than queues: arriving late is worse than not arriving.
        registry.RegisterOneShot("warp.position", args =>
        {
            var block = args.Unsigned("block");
            var x = args.Real("x");
            var y = args.Real("y");
            var z = args.Real("z");
            if (block == null || x == null || y == null || z == null)
                throw new OperationRefused("warp.position wants a block and x, y, z");
            var angle = args.Real("angle") ?? 0f;
            _travel.WarpToBlockId(new Position(block.Value, new Vector3(x.Value, y.Value, z.Value), angle));
        });

        /**
         * Somewhere by name, which is the only kind of somewhere a source
         * can honestly ask for.
         */
        registry.RegisterOneShot("warp.grace", args =>
        {
            var name = (args.Text("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new OperationRefused("warp.grace wants a name");
            var area = (args.Text("area") ?? string.Empty).Trim();
            var found = _graces.Value
                .Where(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                .Where(g => area.Length == 0 || string.Equals(g.MainArea, area, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (found.Count == 0) throw new OperationRefused("no grace called " + name + (area.Length > 0 ? " in " + area : string.Empty));
            // Several places share a name. Guessing which one would move
            // somebody to the wrong side of the map, so it asks instead.
            if (found.Count > 1) throw new OperationRefused(name + " names " + found.Count + " graces; say which area");
            _travel.Warp(found[0]);
        });

        /**
         * Straight up, and then gravity.
         *
         * The one destination that needs no map at all: wherever the
         * player is, a few hundred feet above it. What happens next is
         * the game's business and is usually fatal, which is the point.
         */
        registry.RegisterOneShot("player.drop", args =>
        {
            var height = args.Real("height") ?? 150f;
            if (height < 5f || height > 500f) throw new OperationRefused("player.drop takes 5 to 500");
            var at = _player.GetPlayerPos();
            if (at == Vector3.Zero) throw new OperationRefused("there is no player to lift");
            _player.SetPlayerPos(new Vector3(at.X, at.Y + height, at.Z));
        });

        registry.RegisterOneShot("item.give", args =>
        {
            var id = args.Whole("id");
            if (id == null) throw new OperationRefused("item.give wants an id");
            var quantity = args.Whole("quantity") ?? 1;
            if (quantity < 1 || quantity > 99) throw new OperationRefused("item.give takes 1 to 99");
            _items.SpawnItem(id.Value, quantity, args.Whole("ashOfWar") ?? -1, true, 99);
        });

        registry.RegisterOneShot("action.invoke", args =>
        {
            var name = args.Text("action");
            if (name == null) throw new OperationRefused("action.invoke wants an action");
            if (!Pressable.Any(a => string.Equals(a.ToString(), name, StringComparison.Ordinal)))
                throw new OperationRefused(name + " is not one a source may press");
            if (!_hotkeys.TryInvoke(name)) throw new OperationRefused(name + " is not registered in this build");
        });
    }

    private void Flag(string name, Func<bool> read, Action<bool> write) =>
        _flags[name] = new Toggle { Read = read, Write = write };

    private void Number(string name, Func<float> read, Action<float> write, float least, float most) =>
        _values[name] = new NumberSetting { Read = read, Write = write, Least = least, Most = most };
}
