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
    //   CardPileCmd.AddToCombatAndPreview<Slimed>(targets, pile, count, creator, position)
    //     - generic arg = the status card class shuffled into player piles;
    //       the pile and position enums are exported too, so a consumer can
    //       place the cards exactly instead of assuming discard/bottom
    // Anything unrecognized simply yields no data (consumers fall back to
    // heuristics), and any reflection failure degrades to null.
    //
    // Amount recovery.  Every amount parameter is a Decimal, and C# reaches it
    // three different ways, all of which must be read or the exported number is
    // silently wrong rather than merely absent:
    //   int expression -> op_Implicit(int32)          e.g. HissStrengthGain
    //   0m / 1m / -1m  -> ldsfld Decimal.Zero/One/MinusOne
    //   any other Nm   -> ldc.i4 N; newobj Decimal(int32)
    // The tracked int is therefore cleared the moment IL does arithmetic on it
    // (a computed amount reports null, which consumers flag), negated on `neg`
    // so a stolen stat keeps its sign, and consumed on use so a later effect
    // can never inherit an earlier one's number.

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

    /// <summary>Effects found, plus the IL operand state, for one move scan.</summary>
    private sealed class MoveFxScan
    {
        internal readonly List<Dictionary<string, object?>> Applies = new();
        internal int? Block;
        internal int? Heal;
        /// <summary>Status/generated cards inserted, one entry per insert call.</summary>
        internal readonly List<Dictionary<string, object?>> StatusCards = new();

        /// <summary>Top-of-stack integer; null once arithmetic makes it unknown.</summary>
        internal int? LastInt;
        /// <summary>Integer/enum constants pushed for the call being built up.</summary>
        internal readonly List<int?> IntArgs = new();
        /// <summary>Constants belonging to the call currently being dispatched.</summary>
        internal List<int?> CallArgs = new();

        /// <summary>
        /// Generic argument of the most recent `CreateCard&lt;T&gt;`, naming the card a
        /// following `AddGeneratedCardToCombat` inserts.
        /// </summary>
        internal string? PendingCard;
        /// <summary>
        /// Generic argument of the most recent `ModelDb.Power&lt;T&gt;`, naming the
        /// instanced power a following non-generic `PowerCmd.Apply` applies.
        /// </summary>
        internal string? PendingPower;
        /// <summary>
        /// Whether the creature expression evaluated so far in this argument run
        /// was the monster's own `Creature`.
        /// </summary>
        internal bool LastCreatureIsSelf;
        /// <summary>
        /// The above, sampled when the amount was converted.
        /// </summary>
        /// <remarks>
        /// Arguments evaluate left to right, and every apply overload orders
        /// them target-then-amount-then-applier. Sampling at the amount is
        /// therefore the last moment the flag still describes the TARGET; the
        /// applier that follows is almost always `base.Creature` and would
        /// otherwise make every apply look self-targeted.
        /// </remarks>
        internal bool TargetWasSelf;

        private int? _pendingDecimal;
        private bool _hasPendingDecimal;

        internal bool IsEmpty =>
            Applies.Count == 0 && Block == null && Heal == null && StatusCards.Count == 0;

        internal void PushInt(int? value)
        {
            LastInt = value;
            IntArgs.Add(value);
        }

        /// <summary>Forget the tracked int: an amount we cannot evaluate must
        /// report as unknown rather than as one of its operands.</summary>
        internal void PoisonInt()
        {
            LastInt = null;
            IntArgs.Clear();
        }

        internal void Negate()
        {
            if (IntArgs.Count > 0 && IntArgs[^1] == LastInt)
                IntArgs[^1] = -LastInt;
            LastInt = -LastInt;
        }

        /// <summary>
        /// Folds a binary integer operation over the two most recent pushes.
        /// </summary>
        /// <remarks>
        /// The push list doubles as an operand stack, which is exact for the
        /// straight-line arithmetic these amounts use (`BootUpStrGain * (2 -
        /// StockAmount)`). Anything with an unknown operand, or with too few
        /// operands to be sure what is being combined, poisons instead — a
        /// computed amount must report as unknown, never as an operand.
        /// </remarks>
        internal void BinaryOp(Func<int, int, int?> combine)
        {
            if (IntArgs.Count < 2)
            {
                PoisonInt();
                return;
            }
            int? right = IntArgs[^1];
            int? left = IntArgs[^2];
            IntArgs.RemoveRange(IntArgs.Count - 2, 2);
            int? result = left is int l && right is int r ? combine(l, r) : null;
            if (result == null)
            {
                PoisonInt();
                return;
            }
            PushInt(result);
        }

        internal void SetAmount(int? amount)
        {
            _pendingDecimal = amount;
            _hasPendingDecimal = true;
            TargetWasSelf = LastCreatureIsSelf;
        }

        /// <summary>The amount for the effect being emitted, consuming it so the
        /// next effect cannot inherit this one's number.</summary>
        internal int? TakeAmount()
        {
            var amount = _hasPendingDecimal ? _pendingDecimal : null;
            _pendingDecimal = null;
            _hasPendingDecimal = false;
            return amount;
        }

        /// <summary>Close the argument run: the constants collected so far belong
        /// to the call now being dispatched.</summary>
        internal void EndArgumentRun()
        {
            CallArgs = new List<int?>(IntArgs);
            IntArgs.Clear();
            // The next statement's arguments start fresh, so a loop body cannot
            // inherit the previous iteration's applier.
            LastCreatureIsSelf = false;
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

        var scan = new MoveFxScan();
        foreach (var body in bodies)
            WalkForEffects(body, monster, scan);

        if (scan.IsEmpty)
            return null;
        var fx = new Dictionary<string, object?>();
        if (scan.Applies.Count > 0) fx["applies"] = scan.Applies;
        if (scan.Block != null) fx["block"] = scan.Block;
        if (scan.Heal != null) fx["heal"] = scan.Heal;
        if (scan.StatusCards.Count > 0)
        {
            // "status_card" stays a bare class name for consumers that predate
            // the placement export; "status" carries every insert the move
            // makes, each with its own pile, count and position (Noisebot and
            // Soul Fysh split one move across two different piles).
            fx["status_card"] = scan.StatusCards[0]["card"];
            fx["status"] = scan.StatusCards;
        }
        return fx;
    }

    private static void WalkForEffects(MethodBase method, MonsterModel monster, MoveFxScan scan)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return;
        var module = method.Module;

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
                    scan.PushInt(op - 0x16); i += 1; break;
                case 0x1F: scan.PushInt((int)(sbyte)il[i + 1]); i += 2; break;      // ldc.i4.s
                case 0x20: scan.PushInt(BitConverter.ToInt32(il, i + 1)); i += 5; break; // ldc.i4
                case 0x65: scan.Negate(); i += 1; break;   // neg: `-SpikenAmount` is a steal
                // Arithmetic is folded when both operands are known, so an
                // amount computed from monster stats resolves exactly. Anything
                // it cannot fold poisons the tracked value, which turns a
                // computed amount into an explicit "unknown" the consumer flags
                // instead of whichever operand happened to land last.
                case 0x58 or 0xD6 or 0xD7: scan.BinaryOp((l, r) => l + r); i += 1; break;  // add[.ovf]
                case 0x59 or 0xDA or 0xDB: scan.BinaryOp((l, r) => l - r); i += 1; break;  // sub[.ovf]
                case 0x5A or 0xD8 or 0xD9: scan.BinaryOp((l, r) => l * r); i += 1; break;  // mul[.ovf]
                case 0x5B or 0x5C: scan.BinaryOp((l, r) => r == 0 ? null : l / r); i += 1; break;  // div[.un]
                case 0x5D or 0x5E: scan.BinaryOp((l, r) => r == 0 ? null : l % r); i += 1; break;  // rem[.un]
                case 0x5F or 0x60 or 0x61 or 0x62 or 0x63 or 0x64:          // and/or/xor/shl/shr[.un]
                    scan.PoisonInt(); i += 1; break;
                // Control flow: operands collected on one path cannot be
                // combined with those on another.
                case (>= 0x2C and <= 0x37) or (>= 0x39 and <= 0x44):
                    scan.PoisonInt(); i += InstructionLength(il, i); break;
                case 0x7B:                   // ldfld: `_ritualGain`-style counters
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    scan.PushInt(ReadIntField(module, tok, monster));
                    i += 5; break;
                }
                case 0x73:                   // newobj: `2m` is Decimal..ctor(int32)
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    if (IsDecimalIntCtor(module, tok))
                        scan.SetAmount(scan.LastInt);
                    else
                        scan.IntArgs.Clear();
                    i += 5; break;
                }
                case 0x28: case 0x6F:       // call / callvirt
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    MethodBase? mm = null;
                    try { mm = module.ResolveMethod(tok); } catch { }
                    if (mm != null)
                        HandleCall(mm, monster, scan);
                    i += 5; break;
                }
                case 0x7E:                   // ldsfld: Decimal.One-style constants
                {                            // load with no op_Implicit conversion
                    int tok = BitConverter.ToInt32(il, i + 1);
                    try
                    {
                        var f = module.ResolveField(tok);
                        if (f?.DeclaringType == typeof(decimal))
                        {
                            switch (f.Name)
                            {
                                case "One": scan.SetAmount(1); break;
                                case "Zero": scan.SetAmount(0); break;
                                case "MinusOne": scan.SetAmount(-1); break;
                            }
                        }
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
                        0x72 or 0x7B or 0x7C or 0x7D or 0x7F or 0x80 or 0x81 or 0x82 => 5, // token ops
                        0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 => 5, // more token ops
                        _ => 1,
                    };
                    break;
            }
        }
    }

    private static bool IsDecimalIntCtor(Module module, int token)
    {
        try
        {
            var ctor = module.ResolveMethod(token);
            var pars = ctor?.GetParameters();
            return ctor?.DeclaringType == typeof(decimal)
                && pars?.Length == 1 && pars[0].ParameterType == typeof(int);
        }
        catch
        {
            return false;
        }
    }

    private static void HandleCall(MethodBase mm, MonsterModel monster, MoveFxScan scan)
    {
        // int -> Decimal conversion marks "this int is about to be an amount"
        if (mm.Name == "op_Implicit" && mm.DeclaringType == typeof(decimal))
        {
            scan.SetAmount(scan.LastInt);
            return;
        }
        // Stat getters (get_HissStrengthGain) are resolved against the live
        // monster so ascension scaling comes through; other getters
        // (get_Creature, get_AttackSfx) must NOT clobber the tracked int.
        if (mm.Name.StartsWith("get_") && mm is MethodInfo getter)
        {
            // Which creature an expression produced decides who a non-generic
            // apply targets, since that overload's parameter is always Creature.
            if (getter.Name == "get_Creature")
                scan.LastCreatureIsSelf = true;
            if (getter.ReturnType == typeof(int) && getter.GetParameters().Length == 0)
                scan.PushInt(InvokeIntGetter(getter, monster));
            else
                scan.IntArgs.Clear();
            return;
        }

        // `CreateCard<Dazed>(player)` names the card the next insert adds, and
        // `ModelDb.Power<SandpitPower>()` names the instanced power the next
        // non-generic apply applies. Both are the only static handle on values
        // that are otherwise built at runtime.
        if (mm is MethodInfo generic && generic.IsGenericMethod)
        {
            string? argument = generic.GetGenericArguments().FirstOrDefault()?.Name;
            if (mm.Name == "CreateCard")
                scan.PendingCard = argument;
            else if (mm.Name == "Power" && mm.DeclaringType?.Name == "ModelDb")
                scan.PendingPower = argument;
        }

        scan.EndArgumentRun();

        string owner = mm.DeclaringType?.Name ?? "";
        if (owner == "PowerCmd" && mm.Name == "Apply" && mm is MethodInfo applyMi)
        {
            var pars = applyMi.GetParameters();
            string? powerName;
            bool playerSide;
            if (applyMi.IsGenericMethod)
            {
                powerName = applyMi.GetGenericArguments().FirstOrDefault()?.Name;
                // Apply(ctx, Creature target, ...) buffs a single creature (the
                // monster itself in practice); Apply(ctx, IEnumerable targets,
                // ...) hits the player side.
                playerSide = pars.Length > 1
                    && typeof(IEnumerable).IsAssignableFrom(pars[1].ParameterType)
                    && pars[1].ParameterType != typeof(string);
            }
            else
            {
                // Apply(ctx, PowerModel power, Creature target, ...) applies an
                // instance built at runtime. Its target parameter is a single
                // Creature either way, so who receives it is read from the
                // expression that produced that creature: the monster's own
                // `Creature` property means self, anything else (a loop
                // variable over the move's targets) means the player side.
                powerName = scan.PendingPower;
                playerSide = !scan.TargetWasSelf;
                scan.PendingPower = null;
                if (powerName == null)
                {
                    scan.TakeAmount();
                    return;
                }
            }
            scan.Applies.Add(new Dictionary<string, object?>
            {
                ["power"] = powerName,
                ["target"] = playerSide ? "player" : "self",
                ["amount"] = scan.TakeAmount(),
            });
        }
        else if (owner == "CreatureCmd" && mm.Name == "GainBlock")
        {
            scan.Block = (scan.Block ?? 0) + (scan.TakeAmount() ?? 0);
            if (scan.Block == 0) scan.Block = null;
        }
        else if (owner == "CreatureCmd" && mm.Name.Contains("Heal"))
        {
            scan.Heal = (scan.Heal ?? 0) + (scan.TakeAmount() ?? 0);
            if (scan.Heal == 0) scan.Heal = null;
        }
        else if (owner == "CardPileCmd" && mm.Name.StartsWith("AddToCombat")
                 && mm is MethodInfo cardMi && cardMi.IsGenericMethod)
        {
            scan.StatusCards.Add(BuildStatusCard(
                cardMi.GetGenericArguments().FirstOrDefault()?.Name, cardMi, scan.CallArgs, null));
        }
        else if (owner == "CardPileCmd" && mm.Name.StartsWith("AddGeneratedCard")
                 && mm is MethodInfo generatedMi)
        {
            // One call inserts the single card the preceding `CreateCard<T>`
            // built, so the count is fixed at one and the pile and position come
            // from the call's own arguments.
            scan.StatusCards.Add(BuildStatusCard(scan.PendingCard, generatedMi, scan.CallArgs, 1));
            scan.PendingCard = null;
        }
    }

    /// <summary>
    /// Reads pile, count and position off an <c>AddToCombat*</c> call. The
    /// int-like parameters are the trailing constants pushed for the call, so
    /// they are matched positionally against the signature rather than by a
    /// fixed argument index (the overloads differ in arity, and the optional
    /// position argument is emitted at the call site).
    /// </summary>
    private static Dictionary<string, object?> BuildStatusCard(
        string? cardName, MethodInfo cardMi, List<int?> intArgs, int? fixedCount)
    {
        var card = new Dictionary<string, object?>
        {
            ["card"] = cardName,
            ["pile"] = null,
            ["count"] = fixedCount,
            ["position"] = null,
        };
        var intLike = cardMi.GetParameters()
            .Where(p => p.ParameterType.IsEnum || p.ParameterType == typeof(int))
            .ToList();
        if (intLike.Count == 0 || intArgs.Count < intLike.Count)
            return card;
        // The call's own constants are the tail of the run; anything earlier
        // belongs to a preceding expression.
        var tail = intArgs.GetRange(intArgs.Count - intLike.Count, intLike.Count);
        for (int p = 0; p < intLike.Count; p++)
        {
            var par = intLike[p];
            int? value = tail[p];
            if (value == null)
                continue;
            if (par.ParameterType == typeof(int))
                card["count"] = value;
            else if (par.ParameterType.Name == "PileType")
                card["pile"] = Enum.GetName(par.ParameterType, value.Value);
            else if (par.ParameterType.Name == "CardPilePosition")
                card["position"] = Enum.GetName(par.ParameterType, value.Value);
        }
        return card;
    }

    /// <summary>
    /// The live value of an int field the move reads off its own monster.
    /// </summary>
    /// <remarks>
    /// Some amounts are plain mutable counters rather than design properties
    /// (`DevotedSculptor._ritualGain`). Reading the field is the same live-value
    /// resolution the stat getters already get; a field on any other object is
    /// unknown, which poisons the amount rather than inventing one.
    /// </remarks>
    private static int? ReadIntField(Module module, int token, MonsterModel monster)
    {
        try
        {
            var field = module.ResolveField(token);
            if (field == null || field.FieldType != typeof(int))
                return null;
            if (!field.DeclaringType!.IsInstanceOfType(monster))
                return null;
            return field.GetValue(monster) as int?;
        }
        catch
        {
            return null;
        }
    }

    private static int? InvokeIntGetter(MethodInfo getter, MonsterModel monster)
    {
        try
        {
            object? target = getter.IsStatic ? null
                : getter.DeclaringType!.IsInstanceOfType(monster) ? monster : null;
            if (getter.IsStatic || target != null)
                return getter.Invoke(target, null) as int?;
        }
        catch { }
        return null;
    }
}
