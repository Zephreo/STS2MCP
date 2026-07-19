using System;
using System.Collections;
using System.Collections.Generic;
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
            foreach (var state in machine.States.Values)
            {
                Dictionary<string, object?>? serialized = state switch
                {
                    MoveState move => BuildMoveMachineState(monster, creature, move),
                    RandomBranchState random => BuildRandomMachineState(random),
                    ConditionalBranchState conditional =>
                        BuildConditionalMachineState(monster, conditional, ref hasConditionalSnapshots),
                    _ => null,
                };
                if (serialized == null)
                    continue;
                states.Add(serialized);
            }

            if (states.Count == 0)
                return null;

            var rng = monster.RunRng.MonsterAi;
            var stateLog = new List<string>(machine.StateLog.Count);
            foreach (var logged in machine.StateLog)
                stateLog.Add(logged.Id);

            return new Dictionary<string, object?>
            {
                ["initial_state"] = initial.Id,
                ["current_state"] = current.Id,
                ["state_log"] = stateLog,
                ["rng_seed"] = rng.Seed,
                ["rng_counter"] = rng.Counter,
                ["conditional_values_are_snapshots"] = hasConditionalSnapshots,
                // Two-Tailed Rat is the shipped machine whose branch weights
                // depend on mutable CanSummon state. Other shipped lambdas are
                // constants; modded dynamic lambdas cannot be distinguished
                // generically from constants by reflection.
                ["random_weights_are_snapshots"] = monster.GetType().Name == "TwoTailedRat",
                ["states"] = states,
            };
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

    private static Dictionary<string, object?> BuildRandomMachineState(RandomBranchState random)
    {
        var branches = new List<Dictionary<string, object?>>(random.States.Count);
        foreach (var branch in random.States)
        {
            float weight;
            try { weight = branch.GetWeight(); }
            catch { weight = 0f; }
            branches.Add(new Dictionary<string, object?>
            {
                ["state"] = branch.stateId,
                ["weight"] = weight,
                ["repeat"] = branch.repeatType.ToString(),
                ["max_repeats"] = branch.maxTimes,
                ["cooldown"] = branch.cooldown,
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
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
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
                var condition = BuildCondition(monster, conditional.Id, id, enabled, out bool isSnapshot);
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

    private static Dictionary<string, object?> BuildCondition(
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        string branchId,
        string targetId,
        bool enabled,
        out bool isSnapshot)
    {
        isSnapshot = false;
        string monsterType = monster.GetType().Name;

        // Slot/front/alone branches are fixed when the encounter constructs
        // the monster and can safely remain constants for the whole search.
        if (branchId == "INIT_MOVE")
            return Condition("constant", ("value", enabled));

        if (monsterType == "FrogKnight" && branchId == "HALF_HEALTH")
        {
            var charged = Condition("move_seen", ("move_id", "BEETLE_CHARGE"));
            var hp = Condition("owner_hp_fraction",
                ("cmp", targetId == "TONGUE_LASH" ? "ge" : "lt"),
                ("numerator", 1), ("denominator", 2));
            return targetId == "TONGUE_LASH"
                ? Condition("or", ("args", new[] { charged, hp }))
                : Condition("and", ("args", new[] { Condition("not", ("arg", charged)), hp }));
        }

        if (monsterType == "LivingShield")
            return Condition("living_allies", ("cmp", targetId == "SHIELD_SLAM_MOVE" ? "gt" : "eq"), ("value", 0));

        if (monsterType == "Fabricator")
            return Condition("living_allies", ("cmp", targetId == "RAND" ? "lt" : "ge"), ("value", 4));

        if (monsterType == "Ovicopter")
            return Condition("living_allies", ("cmp", targetId == "LAY_EGGS_MOVE" ? "le" : "gt"), ("value", 3));

        if (monsterType == "Queen")
        {
            var alive = Condition("monster_alive", ("entity_prefix", "TORCH_HEAD_AMALGAM"));
            return targetId == "BURN_BRIGHT_FOR_ME_MOVE" ? alive : Condition("not", ("arg", alive));
        }

        if (monsterType == "KnowledgeDemon")
            return Condition("move_count", ("move_id", "CURSE_OF_KNOWLEDGE_MOVE"),
                ("cmp", targetId == "CURSE_OF_KNOWLEDGE_MOVE" ? "lt" : "ge"), ("value", 3));

        if (monsterType == "TestSubject")
            return Condition("move_count", ("move_id", "RESPAWN_MOVE"),
                ("cmp", targetId == "MULTI_CLAW_MOVE" ? "lt" : "ge"), ("value", 2));

        if (monsterType == "LagavulinMatriarch")
        {
            var asleep = Condition("owner_power", ("power_id", "ASLEEP"));
            return targetId == "SLEEP_MOVE" ? asleep : Condition("not", ("arg", asleep));
        }

        if (monsterType == "SlumberingBeetle")
        {
            var slumber = Condition("owner_power", ("power_id", "SLUMBER"));
            return targetId == "SNORE_MOVE" ? slumber : Condition("not", ("arg", slumber));
        }

        isSnapshot = true;
        return Condition("snapshot", ("value", enabled));
    }

    private static Dictionary<string, object?> Condition(
        string op,
        params (string key, object? value)[] fields)
    {
        var result = new Dictionary<string, object?> { ["op"] = op };
        foreach (var (key, value) in fields)
            result[key] = value;
        return result;
    }
}
