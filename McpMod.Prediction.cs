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
            foreach (var state in machine.States.Values)
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
                // Set when a weight lambda was seen to read live state, rather
                // than for a named monster: any shipped or modded machine whose
                // weights move is detected the same way.
                ["random_weights_are_snapshots"] = hasWeightSnapshots,
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

    private static Dictionary<string, object?> BuildRandomMachineState(RandomBranchState random, ref bool hasSnapshots)
    {
        var branches = new List<Dictionary<string, object?>>(random.States.Count);
        foreach (var branch in random.States)
        {
            float weight;
            try { weight = branch.GetWeight(); }
            catch { weight = 0f; }
            // A weight that reads live state cannot be represented by the
            // single sample taken here; say so instead of freezing it silently.
            hasSnapshots |= IsDynamicWeight(branch.weightLambda);
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

    /// <summary>
    /// Translates one branch predicate into the re-evaluable condition language.
    /// </summary>
    /// <remarks>
    /// The comparison and threshold always come from the predicate's own IL.
    /// The member-to-quantity mapping below is the part that cannot: it records
    /// what a mutable monster field counts, which is established in the move
    /// that writes it, not in the predicate that reads it. Each entry cites the
    /// source that justifies it. An unrecognized predicate falls through to a
    /// snapshot, which the consumer already treats as inexact.
    /// </remarks>
    private static Dictionary<string, object?> BuildCondition(
        Delegate? lambda, int branchIndex, bool enabled, out bool isSnapshot)
    {
        isSnapshot = false;
        var scan = ScanPredicate(lambda?.Method);
        bool negated = IsNegated(scan);

        // Encounter-fixed placement: the slot a creature occupies, and the
        // `IsFront` / `IsAlone` flags the encounter sets before combat, never
        // change once the monster exists. The sampled value is therefore exact
        // for the whole search rather than a snapshot of moving state.
        if (ReadsAny(scan, ".get_SlotName", ".get_IsFront", ".get_IsAlone"))
            return Condition("constant", ("value", enabled));

        // `Creature.HasPower<T>()`: the generic argument names the power.
        if (scan.Reads("Creature.HasPower") && scan.GenericArgs.Count > 0)
            return Negate(Condition("owner_power", ("power_id", PowerIdFromClass(scan.GenericArgs[0]))), negated);

        // FrogKnight: branch 0 is `HasBeetleCharged || CurrentHp >= MaxHp / 2`
        // and branch 1 is its exact complement, `!HasBeetleCharged && CurrentHp
        // < MaxHp / 2`. Emitting the complement as `not` follows the source's
        // own branch order, rather than guessing how the compiler lowered a
        // short-circuit into branch opcodes. C# integer division floors, so the
        // threshold is floor(MaxHp/denominator), not the exact rational.
        if (scan.Reads("FrogKnight.get_HasBeetleCharged"))
        {
            int denominator = scan.Ints.FirstOrDefault(value => value > 1);
            if (denominator <= 1)
                denominator = 2;
            var charged = Condition("move_seen", ("move_id", "BEETLE_CHARGE"));
            var half = Condition("owner_hp_fraction",
                ("cmp", "ge"), ("numerator", 1), ("denominator", denominator), ("floor", true));
            var either = Condition("or", ("args", new[] { charged, half }));
            return branchIndex == 0 ? either : Condition("not", ("arg", either));
        }

        // Living-teammate counts. `GetTeammatesOf` returns the whole side, so a
        // count includes the owner unless its LINQ predicate rejects it; the
        // consumer counts other living enemies, and `include_self` tells it
        // which of the two the threshold was written against. The comparison and
        // constant may sit one call deeper, in the bool property the predicate
        // reads.
        var inlined = InlineMonsterCall(scan);
        if (scan.Reads("CombatState.GetTeammatesOf") || scan.Reads("ICombatState.GetTeammatesOf")
            || inlined?.Reads("CombatState.GetTeammatesOf") == true
            || inlined?.Reads("ICombatState.GetTeammatesOf") == true)
            return BuildAllyCountCondition(scan, inlined, negated, ref isSnapshot);

        // Counters a move increments once per performance, so the machine's own
        // move log reproduces them exactly.
        //   KnowledgeDemon._curseOfKnowledgeCounter -> CurseOfKnowledgeMove
        //   TestSubject.Respawns                    -> RespawnMove
        if (scan.Reads("KnowledgeDemon._curseOfKnowledgeCounter"))
            return BuildMoveCountCondition(scan, negated, "CURSE_OF_KNOWLEDGE_MOVE", ref isSnapshot);
        if (scan.Reads("TestSubject.get_Respawns"))
            return BuildMoveCountCondition(scan, negated, "RESPAWN_MOVE", ref isSnapshot);

        // Queen.HasAmalgamDied is latched in AfterDeath when a TorchHeadAmalgam
        // dies, so "not yet died" is "an amalgam is still alive".
        if (scan.Reads("Queen.get_HasAmalgamDied"))
        {
            var alive = Condition("monster_alive", ("entity_prefix", "TORCH_HEAD_AMALGAM"));
            return negated ? alive : Condition("not", ("arg", alive));
        }

        // BowlbugRock.IsOffBalance is set by ImbalancedPower when the owner's
        // own attack is fully blocked, which the consumer already tracks as the
        // pending Imbalanced stun.
        if (scan.Reads("BowlbugRock.get_IsOffBalance"))
            return Negate(Condition("owner_power", ("power_id", "IMBALANCED_STUN")), negated);

        isSnapshot = true;
        return Condition("snapshot", ("value", enabled));
    }

    private static Dictionary<string, object?> BuildAllyCountCondition(
        PredicateScan scan, PredicateScan? inlined, bool negated, ref bool isSnapshot)
    {
        // The predicate owns the comparison when it makes one itself
        // (`GetAllyCount() > 0`); otherwise it just reads a bool property and
        // the comparison belongs to that property's body.
        bool comparesDirectly = scan.Comparisons.Any(comparison => comparison != "ceq")
            || (scan.Comparisons.Count > 0 && !TestsBoolean(scan));
        var source = comparesDirectly || inlined == null ? scan : inlined;
        // An inner `<=` is itself a negated `>`, and an outer `!` inverts that
        // again, so the two compose.
        bool effectiveNegation = ReferenceEquals(source, scan) ? negated : IsNegated(source) ^ negated;

        string? cmp = SourceComparison(source, effectiveNegation);
        int? value = Threshold(source);
        if (cmp == null || value == null)
        {
            isSnapshot = true;
            return Condition("snapshot", ("value", false));
        }
        return Condition("living_allies",
            ("cmp", cmp), ("value", value.Value), ("include_self", !CountExcludesSelf(scan, inlined)));
    }

    private static Dictionary<string, object?> BuildMoveCountCondition(
        PredicateScan scan, bool negated, string moveId, ref bool isSnapshot)
    {
        string? cmp = SourceComparison(scan, negated);
        int? value = Threshold(scan);
        if (cmp == null || value == null)
        {
            isSnapshot = true;
            return Condition("snapshot", ("value", false));
        }
        return Condition("move_count", ("move_id", moveId), ("cmp", cmp), ("value", value.Value));
    }

    private static Dictionary<string, object?> Negate(Dictionary<string, object?> condition, bool negated) =>
        negated ? Condition("not", ("arg", condition)) : condition;

    /// <summary>`AsleepPower` -&gt; `ASLEEP`, matching the consumer's power ids.</summary>
    private static string PowerIdFromClass(string className)
    {
        string stripped = className.EndsWith("Power") ? className[..^"Power".Length] : className;
        var id = new System.Text.StringBuilder(stripped.Length + 4);
        for (int i = 0; i < stripped.Length; i++)
        {
            if (i != 0 && char.IsUpper(stripped[i]))
                id.Append('_');
            id.Append(char.ToUpperInvariant(stripped[i]));
        }
        return id.ToString();
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
