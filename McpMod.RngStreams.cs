using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // RNG export.
    //
    // The game holds deterministic randomness in three places, and all three are
    // exported here so a consumer can reconstruct any stream at its current
    // position and roll it forward without ever touching the live generator —
    // the same technique BuildIntentMachine already uses for RunRng.MonsterAi.
    //
    //   1. RunRngSet (runState.Rng) - the run-wide streams, one per RunRngType:
    //      UpFront, Shuffle, UnknownMapPoint, CombatCardGeneration,
    //      CombatPotionGeneration, CombatCardSelection, CombatEnergyCosts,
    //      CombatTargets, MonsterAi, Niche, CombatOrbs, TreasureRoomRelics.
    //      Niche is the one that rolls a spawned monster's HP
    //      (CombatState.CreateCreature -> Creature.SetUniqueMonsterHpValue),
    //      so mid-combat summons (Infested wrigglers, Surprise gremlins) are
    //      predictable from it.
    //   2. PlayerRngSet (player.PlayerRng) - per-player Rewards / Shops /
    //      Transformations, seeded from the run seed plus the player's SLOT
    //      index. Exported per player alongside that player's odds.
    //   3. MonsterModel.Rng - a per-creature stream assigned in
    //      CombatState.CreateCreature, seeded from the run seed and the current
    //      map coordinate. Used for monster-local randomness that wants to stay
    //      synced between players.
    //
    // The stateful odds accumulators (RunOddsSet / PlayerOddsSet) ride along
    // with their streams: a raw counter is not enough to predict an unknown map
    // point or a card-rarity roll without the running odds value that the roll
    // is compared against.
    //
    // In multiplayer the host syncs the whole RunRngSet at combat start
    // (SyncRngMessage) and lockstep keeps counters aligned, so the local set
    // reflects every player's consumption.
    //
    // Everything is read via cached reflection so a game update that renames a
    // stream degrades to omitting that stream rather than crashing.

    private static PropertyInfo? _rngSeedProp;
    private static PropertyInfo? _rngCounterProp;

    private static readonly Dictionary<Type, PropertyInfo[]> _rngSetProps = new();
    private static readonly Dictionary<Type, FieldInfo?> _rngSetDictField = new();
    private static readonly Dictionary<Type, PropertyInfo[]> _oddsSetProps = new();
    private static readonly Dictionary<Type, PropertyInfo[]> _oddsValueProps = new();

    /// <summary>
    /// Exports the run-wide RNG streams plus the run seed and the run odds.
    /// Never advances any stream.
    /// </summary>
    private static Dictionary<string, object?>? BuildRngStreams(RunState runState)
    {
        try
        {
            var rngSet = runState.Rng;
            if (rngSet == null)
                return null;

            var streams = new Dictionary<string, object?>();

            // The base run seed and its input string. The map LAYOUT is not one of
            // the enumerated streams: StandardActMap builds from a fresh
            // `new Rng(runState.Rng.Seed, $"act_{act}_map")` derived purely from
            // this seed, so a consumer can reconstruct the whole layout (and any
            // other ad-hoc `new Rng(Seed, name)` stream) from run_seed alone.
            streams["run_seed"] = rngSet.Seed;
            streams["run_string_seed"] = rngSet.StringSeed;

            SweepRngSet(rngSet, streams);

            // UnknownMapPoint's odds accumulate across the act, so the counter
            // alone cannot tell you what a "?" node will roll into.
            var odds = BuildOddsSet(SafeGet(() => (object?)runState.Odds));
            if (odds != null)
                streams["odds"] = odds;

            return streams.Count > 0 ? streams : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exports one player's RNG streams (Rewards / Shops / Transformations),
    /// their seed, their slot index and their odds accumulators.
    /// </summary>
    /// <remarks>
    /// The slot index matters beyond bookkeeping: ad-hoc content RNGs built via
    /// <c>new Rng(player, id, mixin)</c> are seeded from the run seed plus the
    /// player SLOT index plus the content id, so a consumer needs the slot to
    /// reconstruct them.
    /// </remarks>
    private static Dictionary<string, object?>? BuildPlayerRngStreams(Player player)
    {
        try
        {
            var rngSet = player.PlayerRng;
            if (rngSet == null)
                return null;

            var streams = new Dictionary<string, object?>();

            var seedProp = rngSet.GetType().GetProperty("Seed", BindingFlags.Public | BindingFlags.Instance);
            var seed = seedProp?.GetValue(rngSet);
            if (seed != null)
                streams["seed"] = seed;

            var slot = SafeGet(() => (object?)player.RunState.GetPlayerSlotIndex(player));
            if (slot != null)
                streams["slot_index"] = slot;

            SweepRngSet(rngSet, streams);

            var odds = BuildOddsSet(SafeGet(() => (object?)player.PlayerOdds));
            if (odds != null)
                streams["odds"] = odds;

            return streams.Count > 0 ? streams : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exports a creature's own monster-local RNG stream, assigned when the
    /// creature was created. Canonical (non-mutable) models hand back the
    /// non-deterministic <c>Rng.Chaotic</c> instance, which is worthless to a
    /// consumer, so those are omitted.
    /// </summary>
    private static Dictionary<string, object?>? BuildMonsterRng(Creature creature)
    {
        try
        {
            var monster = creature.Monster;
            if (monster == null || !monster.IsMutable)
                return null;
            return RngCoords(monster.Rng);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes every <c>Rng</c> held by an RNG set into <paramref name="into" />.
    ///
    /// Public <c>Rng</c>-typed properties come first (their names are the stable
    /// contract consumers key off), then the set's private backing dictionary is
    /// swept for any stream that has no property accessor — so a game update that
    /// adds an RngType still exports it, under the snake-cased enum name.
    /// Streams already emitted by a property are matched by reference, not by
    /// name, so the same stream is never exported twice under two spellings.
    /// </summary>
    private static void SweepRngSet(object rngSet, Dictionary<string, object?> into)
    {
        var type = rngSet.GetType();

        if (!_rngSetProps.TryGetValue(type, out var props))
        {
            var found = new List<PropertyInfo>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType.Name == "Rng" && prop.CanRead && prop.GetIndexParameters().Length == 0)
                    found.Add(prop);
            }
            props = found.ToArray();
            _rngSetProps[type] = props;
        }

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var prop in props)
        {
            var rng = SafeGet(() => prop.GetValue(rngSet));
            if (rng == null)
                continue;
            var coords = RngCoords(rng);
            if (coords == null)
                continue;
            seen.Add(rng);
            into[SnakeCase(prop.Name)] = coords;
        }

        if (!_rngSetDictField.TryGetValue(type, out var dictField))
        {
            dictField = null;
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (typeof(IDictionary).IsAssignableFrom(field.FieldType))
                {
                    dictField = field;
                    break;
                }
            }
            _rngSetDictField[type] = dictField;
        }

        if (dictField == null)
            return;

        if (SafeGet(() => dictField.GetValue(rngSet)) is not IDictionary rngs)
            return;

        foreach (DictionaryEntry entry in rngs)
        {
            if (entry.Value == null || seen.Contains(entry.Value))
                continue;
            var coords = RngCoords(entry.Value);
            if (coords == null)
                continue;
            var name = entry.Key?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            into[SnakeCase(name!)] = coords;
        }
    }

    /// <summary>
    /// {seed, counter} for a single Rng, or null if it does not look like one.
    /// </summary>
    private static Dictionary<string, object?>? RngCoords(object? rng)
    {
        if (rng == null)
            return null;

        _rngSeedProp ??= rng.GetType().GetProperty("Seed", BindingFlags.Public | BindingFlags.Instance);
        _rngCounterProp ??= rng.GetType().GetProperty("Counter", BindingFlags.Public | BindingFlags.Instance);

        var seed = SafeGet(() => _rngSeedProp?.GetValue(rng));
        var counter = SafeGet(() => _rngCounterProp?.GetValue(rng));
        if (seed == null || counter == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["seed"] = seed,
            ["counter"] = counter,
        };
    }

    /// <summary>
    /// Exports an odds set as {odds_name: {value_name: float}}. Odds objects are
    /// plain float bags (CurrentValue on the base, plus per-room-type odds on
    /// UnknownMapPointOdds), so every readable public float is emitted rather
    /// than a hand-maintained list.
    /// </summary>
    private static Dictionary<string, object?>? BuildOddsSet(object? oddsSet)
    {
        if (oddsSet == null)
            return null;

        var type = oddsSet.GetType();
        if (!_oddsSetProps.TryGetValue(type, out var props))
        {
            var found = new List<PropertyInfo>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.CanRead && prop.GetIndexParameters().Length == 0 && !prop.PropertyType.IsPrimitive)
                    found.Add(prop);
            }
            props = found.ToArray();
            _oddsSetProps[type] = props;
        }

        var result = new Dictionary<string, object?>();
        foreach (var prop in props)
        {
            var odds = SafeGet(() => prop.GetValue(oddsSet));
            var values = OddsValues(odds);
            if (values != null)
                result[SnakeCase(prop.Name)] = values;
        }

        return result.Count > 0 ? result : null;
    }

    private static Dictionary<string, object?>? OddsValues(object? odds)
    {
        if (odds == null)
            return null;

        var type = odds.GetType();
        if (!_oddsValueProps.TryGetValue(type, out var props))
        {
            var found = new List<PropertyInfo>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType == typeof(float) && prop.CanRead && prop.GetIndexParameters().Length == 0)
                    found.Add(prop);
            }
            props = found.ToArray();
            _oddsValueProps[type] = props;
        }

        if (props.Length == 0)
            return null;

        var values = new Dictionary<string, object?>();
        foreach (var prop in props)
        {
            var value = SafeGet(() => prop.GetValue(odds));
            if (value != null)
                values[SnakeCase(prop.Name)] = value;
        }

        return values.Count > 0 ? values : null;
    }

    private static object? SafeGet(Func<object?> get)
    {
        try { return get(); }
        catch { return null; }
    }

    private static string SnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
