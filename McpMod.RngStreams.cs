using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using MegaCrit.Sts2.Core.Context;
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
    // Relics are the one predictable thing a stream cannot describe. They come
    // out of Player.RelicGrabBag - per-rarity deques shuffled ONCE at run start
    // off RunRng.UpFront and then mutated for the rest of the run by every
    // source that hands out a relic. Reconstructing that from the seed would
    // mean replaying every pull, every RelicCmd.Obtain, and every
    // IsAllowed prune since floor 1, and a single miss desyncs it silently for
    // the rest of the run - so the deques are exported directly instead. See
    // BuildRelicGrabBag.
    //
    // In multiplayer the host syncs the whole RunRngSet at combat start
    // (SyncRngMessage) and lockstep keeps counters aligned, so the local set
    // reflects every player's consumption.
    //
    // Everything is read via cached reflection so a game update that renames a
    // stream degrades to omitting that stream rather than crashing.

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
            var runSeed = SafeGet(() => GetInstanceMemberValue(rngSet, "Seed"));
            if (runSeed != null)
                streams["run_seed"] = runSeed;
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
    /// Stable builds expose {seed, counter}; beta builds expose {state, counter}.
    /// </summary>
    private static Dictionary<string, object?>? RngCoords(object? rng)
    {
        if (rng == null)
            return null;

        // Main-branch builds through v0.107 expose the constructor seed and a
        // consumed-value counter directly. Keep that wire shape unchanged.
        var seed = SafeGet(() => GetInstanceMemberValue(rng, "Seed"));
        var counter = SafeGet(() => GetInstanceMemberValue(rng, "Counter"));
        if (seed != null && counter != null)
        {
            return new Dictionary<string, object?>
            {
                ["seed"] = seed,
                ["counter"] = counter,
            };
        }

        // v0.110 made the seed unrecoverable and serializes the live xoshiro
        // state instead. Reflection keeps this DLL loadable on older builds,
        // where neither ToSerializable nor SerializableRng exists.
        var serialized = SafeGet(() => rng.GetType()
            .GetMethod("ToSerializable", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?.Invoke(rng, null));
        if (serialized == null)
            return null;

        counter = SafeGet(() => GetInstanceMemberValue(serialized, "counter"));
        var state = new object?[4];
        for (int i = 0; i < state.Length; i++)
            state[i] = SafeGet(() => GetInstanceMemberValue(serialized, $"state{i}"));

        if (counter == null || Array.Exists(state, word => word == null))
            return null;

        return new Dictionary<string, object?>
        {
            ["state"] = state,
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

    /// <summary>
    /// GET /api/v1/relicbag - every player's relic deques, in draw order.
    /// </summary>
    /// <remarks>
    /// Its own endpoint rather than a field on the state, because the two have
    /// opposite shapes. The bag is ~1.8 KB of relic ids at run start and the
    /// state is polled ten times a second, but the bag only CHANGES when a
    /// relic leaves it — at an elite, a chest, an event, a shop — so a consumer
    /// wants it a handful of times per act, not a hundred times a floor.
    ///
    /// Deliberately NOT called "relicpools", for all that it sits beside
    /// /api/v1/cardpools: a card pool is static for a whole run and is fetched
    /// once, while this is a BAG that shrinks every time a relic leaves it.
    /// Never cache it across rooms — fetch it when planning a route and again
    /// when a shop opens.
    /// </remarks>
    private static void HandleGetRelicBag(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(BuildRelicBags);
            SendJson(response, dataTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to build relic bags: {ex.Message}");
        }
    }

    /// <summary>
    /// Every player's bag, keyed by slot so a multiplayer consumer can tell
    /// whose shop it is predicting.
    /// </summary>
    private static Dictionary<string, object?> BuildRelicBags()
    {
        var runState = SafeGet(() => (object?)RunManager.Instance.DebugOnlyGetState()) as RunState;
        if (runState == null)
            return new Dictionary<string, object?> { ["error"] = "No run in progress." };

        var players = new List<Dictionary<string, object?>>();
        foreach (var player in runState.Players)
        {
            var bag = BuildRelicGrabBag(player);
            if (bag == null)
                continue;
            var entry = new Dictionary<string, object?>
            {
                ["net_id"] = SafeGet(() => (object?)player.NetId),
                ["slot_index"] = SafeGet(() => (object?)runState.GetPlayerSlotIndex(player)),
                ["is_me"] = LocalContext.IsMe(player),
                ["deques"] = bag,
            };
            players.Add(entry);
        }

        return new Dictionary<string, object?> { ["players"] = players };
    }

    /// <summary>
    /// Exports one player's relic grab bag: the per-rarity deques that every
    /// relic in the run is drawn from, in order.
    /// </summary>
    /// <remarks>
    /// ORDER IS THE POINT, and so is both ends of it. Reward sources take the
    /// front (RelicFactory.PullNextRelicFromFront - elites, chests, events,
    /// ancients) while a merchant takes the BACK
    /// (MerchantRelicEntry.FillSlot -> PullNextRelicFromBack, filtered to
    /// IsAllowedInShops), so exporting a head or a summary would predict the
    /// wrong half of the run.
    ///
    /// The bag is per PLAYER, which is what multiplayer needs: every player
    /// draws from their own deques. RunState.SharedRelicGrabBag is deliberately
    /// NOT exported - every use of it in the game is a Remove() and nothing
    /// ever pulls from it, so it is a de-duplication ledger rather than a
    /// source. The MP fallback queue IS exported: NTreasureRoomRelicCollection
    /// moves a relic a teammate claimed into it, and GetAvailableDeque falls
    /// back to it once a rarity is exhausted.
    ///
    /// Skipped: relics the bag has already lost. A pull removes its relic
    /// immediately, so a reward the player declined is gone from these lists
    /// without ever appearing in player.relics - which is exactly why the lists
    /// are exported rather than inferred from what the player is holding.
    /// </remarks>
    private static Dictionary<string, object?>? BuildRelicGrabBag(Player player)
    {
        try
        {
            var bag = player.RelicGrabBag;
            if (bag == null || !bag.IsPopulated)
                return null;

            var result = new Dictionary<string, object?>();

            // ToSerializable is the game's own public view of the deques and
            // keeps their order, so this cannot drift from what the save holds.
            var save = bag.ToSerializable();
            foreach (var pair in save.RelicIdLists)
            {
                var ids = new List<string>();
                foreach (var id in pair.Value)
                    ids.Add(id.Entry);
                result[SnakeCase(pair.Key.ToString())] = ids;
            }

            var fallback = ReadMpFallback(bag);
            if (fallback != null && fallback.Count > 0)
                result["mp_fallback"] = fallback;

            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The multiplayer fallback queue, which ToSerializable does not carry.
    /// </summary>
    private static List<string>? ReadMpFallback(object bag)
    {
        try
        {
            var field = bag.GetType().GetField(
                "_mpFallbackDequeue", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(bag) is not IEnumerable relics)
                return null;

            var ids = new List<string>();
            foreach (var relic in relics)
            {
                var idProp = relic?.GetType().GetProperty("Id");
                var id = idProp?.GetValue(relic);
                var entry = id?.GetType().GetProperty("Entry")?.GetValue(id) as string;
                if (entry != null)
                    ids.Add(entry);
            }
            return ids;
        }
        catch
        {
            return null;
        }
    }
}
