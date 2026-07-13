using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace STS2_MCP;

public static partial class McpMod
{
    // Move-effect extraction: what a monster move ACTUALLY does, statically
    // read from the move lambda's IL.  Game code funnels every effect through
    // a handful of command methods, so the calls are recognizable:
    //   PowerCmd.Apply<StrengthPower>(ctx, target|targets, amount, ...)
    //     - generic arg = the power class; the target parameter's type tells
    //       who receives it (Creature = the monster itself / a single ally,
    //       IEnumerable = the player side)
    //   CreatureCmd.GainBlock(creature, amount, ...)
    //   CardPileCmd.AddToCombatAndPreview<Slimed>(targets, pile, count, ...)
    //     - generic arg = the status card class shuffled into player piles
    // Amounts are int constants or monster stat getters (get_HissStrengthGain)
    // converted to Decimal right before the call — the op_Implicit conversion
    // is the capture point; getters are invoked on the live monster instance.
    // Anything unrecognized simply yields no data (consumers fall back to
    // heuristics), and any reflection failure degrades to null.

    private static readonly FieldInfo? _moveOnPerformField =
        typeof(MoveState).GetField("_onPerform", BindingFlags.NonPublic | BindingFlags.Instance);

    // (concrete monster type, move id) -> effects dict (null = nothing found)
    private static readonly Dictionary<(Type, string), Dictionary<string, object?>?> _moveFxCache = new();

    internal static Dictionary<string, object?>? GetMoveEffects(MonsterModel monster, MoveState move)
    {
        try
        {
            var key = (monster.GetType(), move.Id);
            if (_moveFxCache.TryGetValue(key, out var cached))
                return cached;
            var fx = ScanMoveEffects(monster, move);
            _moveFxCache[key] = fx;
            return fx;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?>? ScanMoveEffects(MonsterModel monster, MoveState move)
    {
        if (_moveOnPerformField?.GetValue(move) is not Delegate onPerform)
            return null;
        var stub = onPerform.Method;

        // Async lambdas compile to a stub that spins up a state machine nested
        // in the same type — the real body is its MoveNext.
        var bodies = new List<MethodBase> { stub };
        var smType = stub.DeclaringType?
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(t => t.Name.StartsWith($"<{stub.Name}>d__"));
        var moveNext = smType?.GetMethod("MoveNext",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (moveNext != null)
            bodies.Add(moveNext);

        var applies = new List<Dictionary<string, object?>>();
        int? block = null, heal = null;
        string? statusCard = null;

        foreach (var body in bodies)
            WalkForEffects(body, monster, applies, ref block, ref heal, ref statusCard);

        if (applies.Count == 0 && block == null && heal == null && statusCard == null)
            return null;
        var fx = new Dictionary<string, object?>();
        if (applies.Count > 0) fx["applies"] = applies;
        if (block != null) fx["block"] = block;
        if (heal != null) fx["heal"] = heal;
        if (statusCard != null) fx["status_card"] = statusCard;
        return fx;
    }

    private static void WalkForEffects(MethodBase method, MonsterModel monster,
        List<Dictionary<string, object?>> applies, ref int? block, ref int? heal,
        ref string? statusCard)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return;
        var module = method.Module;

        object? lastInt = null;        // int constant or int-returning getter
        object? pendingDecimal = null; // lastInt at the moment of int->Decimal conversion

        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            if (op == 0xFE)
            {
                byte op2 = il[i + 1];
                // two-byte opcodes: ldftn/ldvirtftn/initobj/constrained etc.
                // carry a 4-byte token; the rest of the ones the compiler
                // emits here are 2-byte (ldloc/stloc/ldarg uint16) or none.
                i += op2 switch
                {
                    0x06 or 0x07 or 0x15 or 0x16 or 0x1C => 6,  // ldftn/ldvirtftn/initobj/constrained/sizeof
                    0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E => 4,  // ldarg/ldarga/starg/ldloc/ldloca/stloc (uint16)
                    _ => 2,
                };
                continue;
            }
            switch (op)
            {
                case >= 0x16 and <= 0x1E:   // ldc.i4.0 .. ldc.i4.8
                    lastInt = op - 0x16; i += 1; break;
                case 0x1F: lastInt = (int)(sbyte)il[i + 1]; i += 2; break;      // ldc.i4.s
                case 0x20: lastInt = BitConverter.ToInt32(il, i + 1); i += 5; break; // ldc.i4
                case 0x28: case 0x6F:       // call / callvirt
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    MethodBase? mm = null;
                    try { mm = module.ResolveMethod(tok); } catch { }
                    if (mm != null)
                        HandleCall(mm, monster, ref lastInt, ref pendingDecimal,
                                   applies, ref block, ref heal, ref statusCard);
                    i += 5; break;
                }
                case 0x7E:                   // ldsfld: Decimal.One-style constants
                {                            // load with no op_Implicit conversion
                    int tok = BitConverter.ToInt32(il, i + 1);
                    try
                    {
                        var f = module.ResolveField(tok);
                        if (f.DeclaringType == typeof(decimal))
                            pendingDecimal = f.Name switch
                            {
                                "One" => 1, "Zero" => 0, "MinusOne" => -1,
                                _ => pendingDecimal,
                            };
                    }
                    catch { }
                    i += 5; break;
                }
                case 0x45:                   // switch: count + count targets
                    i += 5 + 4 * BitConverter.ToInt32(il, i + 1); break;
                default:
                    i += op switch
                    {
                        0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 => 2,       // *.s short-form vars
                        >= 0x2B and <= 0x37 => 2,                                 // short branches
                        0x21 or 0x23 => 9,                                        // ldc.i8 / ldc.r8
                        0x22 => 5,                                                // ldc.r4
                        >= 0x38 and <= 0x44 => 5,                                 // long branches
                        0x72 or 0x73 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x82 => 5, // token ops
                        0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 => 5, // more token ops
                        _ => 1,
                    };
                    break;
            }
        }
    }

    private static void HandleCall(MethodBase mm, MonsterModel monster,
        ref object? lastInt, ref object? pendingDecimal,
        List<Dictionary<string, object?>> applies, ref int? block, ref int? heal,
        ref string? statusCard)
    {
        // int -> Decimal conversion marks "this int is about to be an amount"
        if (mm.Name == "op_Implicit" && mm.DeclaringType == typeof(decimal))
        {
            pendingDecimal = lastInt;
            return;
        }
        // Stat getters (get_HissStrengthGain) become the tracked int source;
        // other getters (get_Creature, get_AttackSfx) must NOT clobber it.
        if (mm.Name.StartsWith("get_") && mm is MethodInfo getter)
        {
            if (getter.ReturnType == typeof(int) && getter.GetParameters().Length == 0)
                lastInt = getter;
            return;
        }

        string owner = mm.DeclaringType?.Name ?? "";
        if (owner == "PowerCmd" && mm.Name == "Apply" && mm is MethodInfo applyMi
            && applyMi.IsGenericMethod)
        {
            var powerType = applyMi.GetGenericArguments().FirstOrDefault();
            var pars = applyMi.GetParameters();
            // Apply(ctx, Creature target, ...) buffs a single creature (the
            // monster itself in practice); Apply(ctx, IEnumerable targets, ...)
            // hits the player side.
            bool playerSide = pars.Length > 1
                && typeof(IEnumerable).IsAssignableFrom(pars[1].ParameterType)
                && pars[1].ParameterType != typeof(string);
            applies.Add(new Dictionary<string, object?>
            {
                ["power"] = powerType?.Name,
                ["target"] = playerSide ? "player" : "self",
                ["amount"] = ResolveAmount(pendingDecimal, monster),
            });
        }
        else if (owner == "CreatureCmd" && mm.Name == "GainBlock")
        {
            block = (block ?? 0) + (ResolveAmount(pendingDecimal, monster) ?? 0);
            if (block == 0) block = null;
        }
        else if (owner == "CreatureCmd" && mm.Name.Contains("Heal"))
        {
            heal = (heal ?? 0) + (ResolveAmount(pendingDecimal, monster) ?? 0);
            if (heal == 0) heal = null;
        }
        else if (owner == "CardPileCmd" && mm.Name.StartsWith("AddToCombat")
                 && mm is MethodInfo cardMi && cardMi.IsGenericMethod)
        {
            statusCard = cardMi.GetGenericArguments().FirstOrDefault()?.Name;
        }
    }

    private static int? ResolveAmount(object? source, MonsterModel monster)
    {
        if (source is int n)
            return n;
        if (source is MethodInfo getter)
        {
            try
            {
                object? target = getter.IsStatic ? null
                    : getter.DeclaringType!.IsInstanceOfType(monster) ? monster : null;
                if (getter.IsStatic || target != null)
                    return getter.Invoke(target, null) as int?;
            }
            catch { }
        }
        return null;
    }
}
