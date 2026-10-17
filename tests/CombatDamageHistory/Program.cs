using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;
using STS2_MCP;

// Exercise the compiled exporter against the real game's history predicates.
// Construct damage results directly; this does not run CreatureCmd or Godot.
var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
var playerState = (PlayerCombatState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCombatState));
var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
Set(owner, "<NetId>k__BackingField", 17UL);
Set(owner, "<PlayerCombatState>k__BackingField", playerState);
Set(owner, "<Creature>k__BackingField", creature);
var state = DispatchProxy.Create<ICombatState, HistoryState>();
var proxy = (HistoryState)(object)state;
proxy.Owner = owner;
var export = typeof(McpMod).GetMethod("DamageNotFullyBlockedThisOwnerTurn", BindingFlags.Static | BindingFlags.NonPublic)!;
bool Export(params DamageReceivedEntry[] entries) => (bool)export.Invoke(null, new object[] { owner, entries })!;

foreach (var (name, props, fullyBlocked, damage) in new[] {
    ("Buffer prevented unblockable Bloodletting", ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, false, 0),
    ("Buffer prevented attack with no Block", ValueProp.Move, false, 0),
    ("Fully blocked attack", ValueProp.Move, true, 0),
    ("HP lost then revived", ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, false, 3)
})
{
    Set(playerState, "<TurnNumber>k__BackingField", 3);
    proxy.Side = CombatSide.Player;
    var result = new DamageResult(creature, props) { UnblockedDamage = damage, WasFullyBlocked = fullyBlocked };
    var entry = new DamageReceivedEntry(result, creature, null, null, 1, CombatSide.Player, null!, new[] { owner });
    if (!entry.HappenedThisTurn(state)) throw new Exception("Current side history missing");
    if (Export(entry) != !fullyBlocked) throw new Exception("Exporter conflated HP loss and full blocking");
    proxy.Side = CombatSide.Enemy;
    if (entry.HappenedThisTurn(state)) throw new Exception("Spite history leaked across sides");
    if (Export(entry) != !fullyBlocked) throw new Exception("Exporter lost owner history across sides");
    Set(playerState, "<TurnNumber>k__BackingField", 4);
    if (!entry.HappenedLastPlayerTurn(owner)) throw new Exception("Previous owner-turn history missing");
    if (Export(entry)) throw new Exception("Old owner-turn damage leaked into the current turn");
    bool emotion = !entry.Result.WasFullyBlocked && entry.HappenedLastPlayerTurn(owner);
    Console.WriteLine($"{name}: Spite={damage > 0}, EmotionChip={emotion}; export and side/owner-turn boundaries agree.");
}
if (Export()) throw new Exception("Empty history must remain false");
Set(playerState, "<TurnNumber>k__BackingField", 4);
var unrelated = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
var otherEntry = new DamageReceivedEntry(new DamageResult(unrelated, ValueProp.Move), unrelated, null, null, 1, CombatSide.Enemy, null!, new[] { owner });
if (Export(otherEntry)) throw new Exception("Another creature's damage activated this owner");
var corrupted = new DamageReceivedEntry(new DamageResult(creature, ValueProp.Move), creature, null, null, 1, CombatSide.Enemy, null!, new[] { owner });
typeof(CombatHistoryEntry).GetField("_playerTurnNumbers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(corrupted, null);
try
{
    Export(corrupted);
    throw new Exception("Unavailable history silently became false");
}
catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { }
Console.WriteLine("Compiled exporter rejects unavailable owner-turn history.");

static void Set(object target, string name, object value) =>
    (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);

public class HistoryState : DispatchProxy
{
    public Player Owner = null!;
    public CombatSide Side;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
        "get_RoundNumber" => 1,
        "get_CurrentSide" => Side,
        "GetPlayer" => (ulong)args![0]! == Owner.NetId ? Owner : null,
        _ => throw new NotSupportedException(method?.Name)
    };
}
