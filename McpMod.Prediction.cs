using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;

namespace STS2_MCP;

public static partial class McpMod
{
    // Future-move prediction (technique from aoyamaY/StS2-MonsterActionPredictor).
    //
    // The enemy AI rng is a seeded counter stream (monster.RunRng.MonsterAi), so
    // upcoming moves are deterministic: clone each monster's move state machine
    // and re-roll it with Rng(seed, current counter).  Unlike the original mod
    // (which rolls each monster independently from the same counter), the rolls
    // are simulated jointly — every living enemy, in combatState.Enemies order,
    // sharing one rng — because the stream is shared and the game rolls enemies
    // sequentially at each turn boundary.  Predictions ignore in-fight events
    // the machine can't see from here (a monster dying early, stuns, HP-phase
    // transitions), so consumers should treat later turns as decreasingly firm.

    private const int FutureMovePredictionTurns = 3;

    private static readonly FieldInfo? _msmCurrentStateField =
        typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? _msmInitialStateField =
        typeof(MonsterMoveStateMachine).GetField("_initialState", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? _msmPerformedFirstMoveField =
        typeof(MonsterMoveStateMachine).GetField("_performedFirstMove", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? _movePerformedAtLeastOnceField =
        typeof(MoveState).GetField("_performedAtLeastOnce", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? _memberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);

    // Cache: predictions only change when the AI rng counter or round advances.
    private static long _predCacheCounter = long.MinValue;
    private static long _predCacheRound = long.MinValue;
    private static readonly Dictionary<Creature, List<Dictionary<string, object?>>> _predCache = new();

    /// <summary>Predicted moves for this enemy's next few turns, oldest first.
    /// Each entry: { "move_id": ..., "intents": [same shape as live "intents"] }.
    /// Null when prediction isn't possible (stunned, no machine, reflection
    /// failure).</summary>
    private static List<Dictionary<string, object?>>? GetPredictedMoves(Creature creature)
    {
        try
        {
            var monster = creature.Monster;
            var combatState = creature.CombatState;
            if (monster?.MoveStateMachine == null || combatState == null)
                return null;

            var counter = monster.RunRng.MonsterAi.Counter;
            var round = combatState.RoundNumber;
            if (counter != _predCacheCounter || round != _predCacheRound)
            {
                RebuildPredictions(combatState, counter);
                _predCacheCounter = counter;
                _predCacheRound = round;
            }
            return _predCache.TryGetValue(creature, out var moves) && moves.Count > 0 ? moves : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RebuildPredictions(ICombatState combatState, int counter)
    {
        _predCache.Clear();

        // Clone every living enemy's machine up front; roll them jointly below.
        var rollOrder = new List<Creature>();
        var machines = new Dictionary<Creature, MonsterMoveStateMachine>();
        foreach (var enemy in combatState.Enemies)
        {
            var m = enemy.Monster;
            if (!enemy.IsAlive || m?.MoveStateMachine == null)
                continue;
            var current = _msmCurrentStateField?.GetValue(m.MoveStateMachine) as MonsterState;
            if (current == null || current.Id == "STUNNED")
                continue;
            var clone = CloneStateMachine(m.MoveStateMachine);
            if (clone == null)
                continue;
            _msmPerformedFirstMoveField?.SetValue(clone, true);
            rollOrder.Add(enemy);
            machines[enemy] = clone;
            _predCache[enemy] = new List<Dictionary<string, object?>>();
        }
        if (rollOrder.Count == 0)
            return;

        var seedMonster = rollOrder[0].Monster!;
        var rng = new Rng(seedMonster.RunRng.MonsterAi.Seed, counter);
        var targets = combatState.PlayerCreatures;

        for (int turn = 0; turn < FutureMovePredictionTurns; turn++)
        {
            foreach (var enemy in rollOrder)
            {
                try
                {
                    var move = machines[enemy].RollMove(targets, enemy, rng);
                    if (move == null)
                        continue;
                    if (move.MustPerformOnceBeforeTransitioning)
                        _movePerformedAtLeastOnceField?.SetValue(move, true);
                    var entry = new Dictionary<string, object?>
                    {
                        ["move_id"] = move.Id,
                        ["intents"] = BuildIntentList(move.Intents, enemy),
                    };
                    var fx = GetMoveEffects(enemy.Monster!, move);
                    if (fx != null)
                        entry["effects"] = fx;
                    _predCache[enemy].Add(entry);
                }
                catch
                {
                    // One enemy failing (unexpected machine shape) shouldn't
                    // sink the others; but its rng draws are unknown from here,
                    // so everyone's later turns are misaligned — stop rolling.
                    return;
                }
            }
        }
    }

    private static MonsterMoveStateMachine? CloneStateMachine(MonsterMoveStateMachine original)
    {
        if (_msmCurrentStateField == null || _msmInitialStateField == null ||
            _msmPerformedFirstMoveField == null || _memberwiseCloneMethod == null)
            return null;

        var clonedStates = new List<MonsterState>();
        foreach (var state in original.States.Values)
            clonedStates.Add((MonsterState)_memberwiseCloneMethod.Invoke(state, null)!);

        var originalInitial = (MonsterState)_msmInitialStateField.GetValue(original)!;
        var clonedInitial = clonedStates.First(s => s.Id == originalInitial.Id);

        var ctor = typeof(MonsterMoveStateMachine).GetConstructor(
            new[] { typeof(IEnumerable<MonsterState>), typeof(MonsterState) });
        if (ctor == null)
            return null;
        var clone = (MonsterMoveStateMachine)ctor.Invoke(new object[] { clonedStates, clonedInitial });

        var originalCurrent = (MonsterState)_msmCurrentStateField.GetValue(original)!;
        _msmCurrentStateField.SetValue(clone, clone.States[originalCurrent.Id]);
        _msmPerformedFirstMoveField.SetValue(clone, (bool)_msmPerformedFirstMoveField.GetValue(original)!);

        // Re-point follow-up references at the cloned states (the shallow copy
        // still points at the live machine's states).
        var followUpProp = typeof(MoveState).GetProperty("FollowUpState");
        foreach (var state in clone.States.Values)
        {
            if (state is MoveState moveState && moveState.FollowUpStateId != null &&
                clone.States.TryGetValue(moveState.FollowUpStateId, out var followUp))
            {
                followUpProp?.SetValue(moveState, followUp);
            }
        }

        return clone;
    }
}
