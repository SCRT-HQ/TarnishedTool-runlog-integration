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
    private readonly IItemService _itemService;
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

    /// <summary>
    /// Every boss the tool can put somebody in front of, by name.
    ///
    /// The other half of the same argument as the graces: these are the
    /// destinations worth naming that are not places anybody rests. A
    /// run that wants to drop a player at Godrick has, until now, had to
    /// ship a block id and three coordinates to say so, which is a thing
    /// only a map can know and a thing that goes wrong quietly when the
    /// game moves.
    /// </summary>
    private readonly Lazy<List<BlockWarp>> _bosses = new(() => DataLoader.GetBossWarps().SelectMany(a => a.Value).ToList());

    /// <summary>
    /// Everything the tool knows how to hand over, by name.
    ///
    /// The same argument as the graces above. A source that wanted to
    /// give somebody a Golden Seed would otherwise have to ship a number
    /// out of the game's own data, and be wrong about it the first time
    /// the game moved. It names the thing instead.
    /// </summary>
    private readonly Lazy<List<Item>> _items = new(() => new[]
        {
            DataLoader.GetItems("Consumables", "Consumables"),
            DataLoader.GetItems("UpgradeMaterials", "Upgrade Materials"),
            DataLoader.GetItems("CraftingMaterials", "Crafting Materials"),
            DataLoader.GetItems("CrystalTears", "Crystal Tears"),
            DataLoader.GetItems("Talismans", "Talismans"),
            DataLoader.GetItems("Armor", "Armor"),
            DataLoader.GetItems("Arrows", "Arrows"),
            DataLoader.GetItems("PotsAndPerfumes", "Pots and Perfumes"),
            DataLoader.GetItems("Sorceries", "Sorceries"),
            DataLoader.GetItems("Incantations", "Incantations"),
            // Key items, but only the ones that are simply given. The
            // rest are tied to an event flag, and handing one over
            // without the event behind it is how a quest breaks.
            DataLoader.GetEventItems("KeyItems", "Key Items").Where(i => !i.NeedsEvent).Cast<Item>().ToList(),
        }
        .SelectMany(x => x)
        .ToList());

    /// <summary>
    /// The weapons, which are not items in the sense above.
    ///
    /// An item is a thing with a name and a count. A weapon is a thing
    /// with a name and a state: its id is the base id plus how far it
    /// has been reinforced, so "Wing of Astel" and "Wing of Astel +10"
    /// are two different numbers and only the second is what somebody
    /// asking for a maxed weapon means. Kept apart from `_items` for
    /// that reason, and given an operation of its own that can be told
    /// the level.
    /// </summary>
    private readonly Lazy<List<Weapon>> _weapons = new(() => DataLoader.GetWeapons());

    /// <summary>
    /// How far a weapon of this kind goes.
    ///
    /// Two scales, and the game's own data says which one a weapon is
    /// on: the ordinary ones take smithing stones to +25, and the ones
    /// that take somber stones stop at +10. Asking for +25 on a somber
    /// weapon is not an error worth refusing, it is somebody meaning
    /// "as far as it goes", so it is held to the top of its own scale.
    /// </summary>
    private static int Ceiling(Weapon weapon) => weapon.UpgradeType == 1 ? 10 : 25;

    /// <summary>Every ash of war, by name, for the weapons that take one.</summary>
    private readonly Lazy<List<AshOfWar>> _ashes = new(() => DataLoader.GetAshOfWars());

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
        _itemService = items;
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
         * In front of a boss, by name.
         *
         * Same shape as a grace and for the same reason. Forty of these
         * are the same fight in two places, so the area says which, and
         * a name that still means two of them is refused rather than
         * guessed: guessing puts somebody on the wrong side of the map.
         */
        registry.RegisterOneShot("warp.boss", args =>
        {
            var name = (args.Text("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new OperationRefused("warp.boss wants a name");
            var area = (args.Text("area") ?? string.Empty).Trim();
            var found = _bosses.Value
                .Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                .Where(b => area.Length == 0 || string.Equals(b.MainArea, area, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (found.Count == 0) throw new OperationRefused("no boss called " + name + (area.Length > 0 ? " in " + area : string.Empty));
            if (found.Count > 1) throw new OperationRefused(name + " names " + found.Count + " arenas; say which area");
            _travel.WarpToBlockId(found[0].Position);
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

        /**
         * Something by name, for the same reason a place is named: an id
         * belongs to one version of one game, and a name does not.
         */
        registry.RegisterOneShot("item.named", args =>
        {
            var name = (args.Text("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new OperationRefused("item.named wants a name");
            var quantity = args.Whole("quantity") ?? 1;
            if (quantity < 1 || quantity > 99) throw new OperationRefused("item.named takes 1 to 99");
            var found = _items.Value.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) throw new OperationRefused("nothing here is called " + name);
            // Several things share a name in this game, and they are
            // usually the same thing; the first will do.
            _itemService.SpawnItem(found[0].Id, Math.Min(quantity, Math.Max(1, found[0].MaxStorage)), -1, true, Math.Max(1, found[0].MaxStorage));
        });

        /**
         * A weapon, by name, at a level.
         *
         * The one thing a run could not ask for: every other gift is a
         * count of something, and a weapon is a thing with a state. The
         * level is the weapon's own, so +10 on a somber weapon is the
         * top of it and +25 on an ordinary one is the top of that; a
         * level past either is held there rather than refused, because
         * somebody typing 25 at a somber weapon means the same thing
         * either way. Omitted, it is the weapon as found.
         */
        registry.RegisterOneShot("weapon.named", args =>
        {
            var name = (args.Text("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new OperationRefused("weapon.named wants a name");
            var found = _weapons.Value.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new OperationRefused("no weapon here is called " + name);
            var asked = args.Whole("upgrade") ?? 0;
            if (asked < 0) throw new OperationRefused("weapon.named takes a level of 0 or more");
            var level = Math.Min(asked, Ceiling(found));

            // An ash of war, where the weapon takes one. A somber weapon
            // does not, and neither does a staff or a seal, so this is
            // refused by name rather than quietly ignored: somebody
            // asking for Bloody Slash on a weapon that cannot hold it
            // wants to know, not to be handed a bare weapon.
            var ashName = (args.Text("ash") ?? string.Empty).Trim();
            var aowId = -1;
            var offset = 0;
            if (ashName.Length > 0)
            {
                if (!found.CanApplyAow) throw new OperationRefused(found.Name + " takes no ash of war");
                var ash = _ashes.Value.FirstOrDefault(a => string.Equals(a.Name, ashName, StringComparison.OrdinalIgnoreCase));
                if (ash == null) throw new OperationRefused("no ash of war is called " + ashName);
                if (!ash.SupportsWeaponType(found.WeaponType)) throw new OperationRefused(ash.Name + " does not go on a " + found.Name);
                aowId = ash.Id;

                // The affinity is part of the weapon's id rather than the
                // ash's, and an ash allows only some of them. Asked for,
                // it is held to that list; unasked, it is the ordinary one
                // where the ash allows it and the ash's own first choice
                // where it does not, since every ash allows something.
                var affinity = (args.Text("affinity") ?? string.Empty).Trim();
                Affinity picked;
                if (affinity.Length > 0)
                {
                    if (!Enum.TryParse(affinity.Replace(" ", string.Empty), true, out picked))
                        throw new OperationRefused("no affinity is called " + affinity);
                    if (!ash.SupportsAffinity(picked)) throw new OperationRefused(ash.Name + " cannot be " + affinity);
                }
                else
                {
                    picked = ash.SupportsAffinity(Affinity.Standard) ? Affinity.Standard : ash.GetAvailableAffinities().First();
                }
                offset = picked.GetIdOffset();
            }

            // A weapon does not stack, so two of them is two of them.
            var count = args.Whole("count") ?? 1;
            if (count < 1 || count > 8) throw new OperationRefused("weapon.named takes 1 to 8");
            for (var i = 0; i < count; i++) _itemService.SpawnItem(found.Id + level + offset, 1, aowId, false, 1);
        });

        /**
         * A number moved by an amount, rather than set to one.
         *
         * Giving somebody five thousand runes is not the same as setting
         * their runes to five thousand, and only one of those is a gift.
         */
        registry.Register("value.add", args =>
        {
            var name = args.Text("name");
            var by = args.Real("by");
            if (name == null || by == null) throw new OperationRefused("value.add wants a name and an amount");
            if (!_values.TryGetValue(name, out var number)) throw new OperationRefused("no such value: " + name);
            var was = number.Read();
            var now = Math.Max(number.Least, Math.Min(number.Most, was + by.Value));
            number.Write(now);
            // Put back what it was, not what it became, in case the
            // player earned some of the difference themselves.
            return new[] { RevertStep.Of("value.set", "name", name, "value", was) };
        });

        registry.RegisterOneShot("item.give", args =>
        {
            var id = args.Whole("id");
            if (id == null) throw new OperationRefused("item.give wants an id");
            var quantity = args.Whole("quantity") ?? 1;
            if (quantity < 1 || quantity > 99) throw new OperationRefused("item.give takes 1 to 99");
            _itemService.SpawnItem(id.Value, quantity, args.Whole("ashOfWar") ?? -1, true, 99);
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
