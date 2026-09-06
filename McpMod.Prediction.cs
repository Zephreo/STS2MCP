using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace STS2_MCP;

public static partial class McpMod
{
    // Monster intent state-machine export.
    //
    // The game keeps the graph topology on MonsterMoveStateMachine, while its
    // current cursor and initial node are private fields. Random branches also
    // carry the repeat/cooldown rules that consult StateLog. Export all of that
    // state so a consumer can advance the machine for an unbounded number of
    // turns with the shared MonsterAi RNG rather than receiving a short,
    // pre-rolled future_moves queue.
    //
    // Conditional branches contain Func<bool> closures into live game objects.
    // Known game predicates are normalized into a small expression language
    // that Rust can re-evaluate against hypothetical combat state. Unknown
    // modded predicates retain their current result as an explicit snapshot.

    private static readonly FieldInfo? _msmCurrentStateField =
        typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? _msmInitialStateField =
        typeof(MonsterMoveStateMachine).GetField("_initialState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo? _conditionalStatesProperty =
        typeof(ConditionalBranchState).GetProperty("States", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Builds the complete intent machine for one enemy.
    /// </summary>
    private static Dictionary<string, object?>? BuildIntentMachine(Creature creature)
    {
        try
        {
            var monster = creature.Monster;
            var machine = monster?.MoveStateMachine;
            var current = _msmCurrentStateField?.GetValue(machine) as MonsterState;
            var initial = _msmInitialStateField?.GetValue(machine) as MonsterState;
            if (monster == null || machine == null || current == null || initial == null)
                return null;

            var states = new List<Dictionary<string, object?>>();
            bool hasConditionalSnapshots = false;
            bool hasWeightSnapshots = false;
            // `States` is built from the list the monster passed to the machine's
            // constructor, which does NOT necessarily contain the machine's own
            // cursor or its initial node. Two shipped shapes escape it:
            //
            //   - `PhantasmalGardener` and `Myte` pass their `INIT_MOVE`
            //     conditional as the initial state without ever adding it to the
            //     list, so the graph's entry point is missing.
            //   - `CreatureCmd.Stun` builds a synthetic `MoveState("STUNNED")`
            //     and installs it with `SetMoveImmediate`, so a stunned monster's
            //     cursor names a state the list never had.
            //
            // Either one leaves the consumer unable to resolve an id it was
            // handed, which costs it the ENTIRE machine and drops that enemy to
            // the intent-DB fallback. Both nodes are perfectly serializable, so
            // they are appended rather than left dangling.
            foreach (var state in machine.States.Values.Concat(new[] { initial, current }).Distinct())
            {
                Dictionary<string, object?>? serialized = state switch
                {
                    MoveState move => BuildMoveMachineState(monster, creature, move),
                    RandomBranchState random => BuildRandomMachineState(random, ref hasWeightSnapshots),
                    ConditionalBranchState conditional =>
                        BuildConditionalMachineState(conditional, ref hasConditionalSnapshots),
                    _ => null,
                };
                if (serialized == null)
                    continue;
                // A monster that DID list its cursor must not export it twice.
                if (states.Any(existing => Equals(existing["id"], serialized["id"])))
                    continue;
                states.Add(serialized);
            }

            if (states.Count == 0)
                return null;

            var rng = monster.RunRng.MonsterAi;
            var rngCoords = RngCoords(rng);
            if (rngCoords == null)
                return null;
            var stateLog = new List<string>(machine.StateLog.Count);
            foreach (var logged in machine.StateLog)
                stateLog.Add(logged.Id);

            var result = new Dictionary<string, object?>
            {
                ["initial_state"] = initial.Id,
                ["current_state"] = current.Id,
                ["state_log"] = stateLog,
                ["rng_counter"] = rngCoords["counter"],
                ["conditional_values_are_snapshots"] = hasConditionalSnapshots,
                // Set when a weight lambda was seen to read live state, rather
                // than for a named monster: any shipped or modded machine whose
                // weights move is detected the same way.
                ["random_weights_are_snapshots"] = hasWeightSnapshots,
                ["states"] = states,
            };
            if (monster.GetType().Name == "TwoTailedRat")
            {
                result["dynamic_weight_state"] = new Dictionary<string, object?>
                {
                    ["turns_until_summonable"] = ReadMonsterInt(monster, "TurnsUntilSummonable"),
                    ["call_for_backup_count"] = ReadMonsterInt(monster, "CallForBackupCount"),
                };
            }
            if (rngCoords.TryGetValue("seed", out var seed))
                result["rng_seed"] = seed;
            if (rngCoords.TryGetValue("state", out var rngState))
                result["rng_state"] = rngState;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildMoveMachineState(
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        Creature creature,
        MoveState move)
    {
        var state = new Dictionary<string, object?>
        {
            ["id"] = move.Id,
            ["kind"] = "move",
            ["follow_up"] = move.FollowUpState?.Id ?? move.FollowUpStateId,
            ["intents"] = BuildIntentList(move.Intents, creature),
        };
        var effects = GetMoveEffects(monster, move);
        if (effects != null)
            state["effects"] = effects;
        return state;
    }

    private static Dictionary<string, object?> BuildRandomMachineState(RandomBranchState random, ref bool hasSnapshots)
    {
        var branches = new List<Dictionary<string, object?>>(random.States.Count);
        foreach (var branch in random.States)
        {
            float weight;
            try { weight = branch.GetWeight(); }
            catch { weight = 0f; }
            string? dynamicRule = DynamicWeightRule(branch.weightLambda);
            // A supported live rule is re-evaluated by the consumer. Any other
            // dynamic lambda remains an explicit snapshot.
            hasSnapshots |= IsDynamicWeight(branch.weightLambda) && dynamicRule == null;
            branches.Add(new Dictionary<string, object?>
            {
                ["state"] = branch.stateId,
                ["weight"] = weight,
                ["repeat"] = branch.repeatType.ToString(),
                ["max_repeats"] = branch.maxTimes,
                ["cooldown"] = branch.cooldown,
                ["weight_rule"] = dynamicRule == null ? null : new Dictionary<string, object?>
                {
                    ["kind"] = dynamicRule,
                    ["summon_branch"] = branch.stateId == "CALL_FOR_BACKUP_MOVE",
                },
            });
        }
        return new Dictionary<string, object?>
        {
            ["id"] = random.Id,
            ["kind"] = "random",
            ["branches"] = branches,
        };
    }

    private static Dictionary<string, object?> BuildConditionalMachineState(
        ConditionalBranchState conditional,
        ref bool hasSnapshots)
    {
        var branches = new List<Dictionary<string, object?>>();
        if (_conditionalStatesProperty?.GetValue(conditional) is IEnumerable values)
        {
            foreach (var value in values)
            {
                var valueType = value.GetType();
                var id = valueType.GetField("id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string;
                var evaluate = valueType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Instance);
                if (id == null || evaluate == null)
                    continue;
                bool enabled;
                try { enabled = Convert.ToSingle(evaluate.Invoke(value, null)) > 0f; }
                catch { enabled = false; }
                var lambda = valueType
                    .GetField("_conditionalLambda", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .GetValue(value) as Delegate;
                var condition = BuildCondition(lambda, branches.Count, enabled, out bool isSnapshot);
                hasSnapshots |= isSnapshot;
                branches.Add(new Dictionary<string, object?>
                {
                    ["state"] = id,
                    ["enabled"] = enabled,
                    ["condition"] = condition,
                });
            }
        }
        return new Dictionary<string, object?>
        {
            ["id"] = conditional.Id,
            ["kind"] = "conditional",
            ["branches"] = branches,
        };
    }
}
