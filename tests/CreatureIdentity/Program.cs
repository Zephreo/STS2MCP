using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using STS2_MCP;

// Exercise the actual game allocator and the mod's compiled exporter without
// constructing a Godot scene or inventing an allocator from visible creatures.
var state = (CombatState)RuntimeHelpers.GetUninitializedObject(typeof(CombatState));
var counter = typeof(CombatState).GetField("_nextCreatureId", BindingFlags.Instance | BindingFlags.NonPublic)!;
var attach = typeof(CombatState).GetMethod("AttachCreature", BindingFlags.Instance | BindingFlags.NonPublic)!;
var export = typeof(McpMod).GetMethod("NextCreatureId", BindingFlags.Static | BindingFlags.NonPublic)!;
counter.SetValue(state, 17u);
foreach (uint expected in new uint[] { 17, 18 })
{
    if ((uint)export.Invoke(null, new object[] { state })! != expected)
        throw new Exception("Exporter lost the exact allocator cursor");
    var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
    attach.Invoke(state, new object[] { creature });
    if (creature.CombatId != expected)
        throw new Exception("Attached creature did not receive the exported next ID");
    // Deliberately never publish the creature via AddCreature.
}
if ((uint)export.Invoke(null, new object[] { state })! != 19u)
    throw new Exception("Unpublished attachments must consume IDs");
counter.SetValue(state, uint.MaxValue);
var last = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
attach.Invoke(state, new object[] { last });
if (last.CombatId != uint.MaxValue || (uint)export.Invoke(null, new object[] { state })! != 0u)
    throw new Exception("Allocator must retain the game's unchecked uint wrap");
Console.WriteLine("Actual game allocation and compiled exporter checks passed.");
