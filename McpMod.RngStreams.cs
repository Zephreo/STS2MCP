using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // Run RNG stream export.
    //
    // The game keeps a per-run RunRngSet of independent xoshiro256** streams
    // (Shuffle, CombatTargets, CombatCardGeneration, ...). Each stream exposes
    // its constructor Seed and the number of values consumed (Counter), so a
    // consumer can reconstruct the stream at its current position and roll it
    // forward without ever touching the live generator — the same technique
    // BuildIntentMachine already uses for RunRng.MonsterAi.
    //
    // In multiplayer the host syncs the whole RunRngSet at combat start
    // (SyncRngMessage) and lockstep keeps counters aligned, so the local set
    // reflects every player's consumption.
    //
    // Properties are read via cached reflection so a game update that renames
    // a stream degrades to omitting that stream rather than crashing.

    private static readonly string[] _rngStreamNames =
    {
        "Shuffle",
        "CombatTargets",
        "CombatCardGeneration",
        "CombatCardSelection",
        "CombatEnergyCosts",
        "CombatOrbGeneration",
        "CombatPotionGeneration",
        "MonsterAi",
        // Run-level (non-combat) streams. Exported so a consumer can predict
        // map/room contents before arriving:
        //   UpFront          - everything rolled at run start: the monster/elite
        //                      encounter order, the event pool, the relic offers.
        //   UnknownMapPoint  - the room TYPE rolled when you step on a "?" node
        //                      (UnknownMapPointOdds.Roll: one NextFloat per visit,
        //                      stateful odds accumulating across the act).
        //   Niche            - one-off run RNG (CursedRun modifier, etc.).
        //   TreasureRoomRelics - MP tie-break when players pick the same relic.
        "UpFront",
        "UnknownMapPoint",
        "Niche",
        "TreasureRoomRelics",
    };

    private static readonly Dictionary<string, PropertyInfo?> _rngStreamProps = new();
    private static PropertyInfo? _rngSeedProp;
    private static PropertyInfo? _rngCounterProp;

    /// <summary>
    /// Exports {seed, counter} per run RNG stream. Never advances any stream.
    /// </summary>
    private static Dictionary<string, object?>? BuildRngStreams(RunState runState)
    {
        try
        {
            var rngSet = runState.Rng;
            if (rngSet == null)
                return null;

            if (_rngStreamProps.Count == 0)
            {
                foreach (var name in _rngStreamNames)
                    _rngStreamProps[name] = typeof(RunRngSet).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            }

            var streams = new Dictionary<string, object?>();

            // The base run seed and its input string. The map LAYOUT is not one of
            // the enumerated streams: StandardActMap builds from a fresh
            // `new Rng(runState.Rng.Seed, $"act_{act}_map")` derived purely from
            // this seed, so a consumer can reconstruct the whole layout (and any
            // other ad-hoc `new Rng(Seed, name)` stream) from run_seed alone.
            streams["run_seed"] = rngSet.Seed;
            streams["run_string_seed"] = rngSet.StringSeed;

            foreach (var name in _rngStreamNames)
            {
                var prop = _rngStreamProps[name];
                var rng = prop?.GetValue(rngSet);
                if (rng == null)
                    continue;

                _rngSeedProp ??= rng.GetType().GetProperty("Seed", BindingFlags.Public | BindingFlags.Instance);
                _rngCounterProp ??= rng.GetType().GetProperty("Counter", BindingFlags.Public | BindingFlags.Instance);
                var seed = _rngSeedProp?.GetValue(rng);
                var counter = _rngCounterProp?.GetValue(rng);
                if (seed == null || counter == null)
                    continue;

                streams[SnakeCase(name)] = new Dictionary<string, object?>
                {
                    ["seed"] = seed,
                    ["counter"] = counter,
                };
            }

            return streams.Count > 0 ? streams : null;
        }
        catch
        {
            return null;
        }
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
