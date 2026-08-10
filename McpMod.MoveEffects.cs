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
    //     - generic arg = the power class. The parameter TYPE alone does not
    //       say who receives it, so the expression that produced the argument
    //       decides, exactly as it does for GainBlock below: a single Creature
    //       is "self" only when it came from the monster's own `Creature`, and
    //       a collection is "player" only when it did not come from
    //       `GetTeammatesOf` (the mover's own side). Anything else exports
    //       "unknown", which the consumer drops and flags — SpectralKnight's
    //       Hex aims a single-Creature apply at the player and TheObscura's
    //       Wail aims a collection apply at its own side, so assuming from the
    //       type silently buffed the wrong creature in both directions.
    //   CreatureCmd.GainBlock(creature, amount, ...)
    //     - the creature expression decides whose Block this is. Nearly every
    //       move shields itself, but `Guardbot.GuardMove` shields
    //       `Enemies.Where(c => c.Monster is Fabricator)` instead, so a
    //       non-self gain is exported separately as "ally_block" rather than
    //       being credited to the mover, which would give a 16 HP bot 15 Block
    //       a turn it never has while the Fabricator's real Block went missing
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
    // The tracked int is therefore cleared the moment IL does arithmetic it
    // cannot fold (a computed amount reports null, which consumers flag),
    // negated on `neg` so a stolen stat keeps its sign, and consumed on use so a
    // later effect can never inherit an earlier one's number.
    //
    // Reading an amount means invoking the getters that produce it against the
    // live monster, which is also how ascension scaling and the run's player
    // count come through.  A getter is therefore resolved against whatever
    // object the surrounding expression has reached, not only against the
    // monster: `30 * base.Creature.CombatState.Players.Count` walks
    // MonsterModel -> Creature -> ICombatState -> IReadOnlyList<Player> before
    // it reaches an int.  Because every step of such a chain is a callvirt that
    // pops its receiver and pushes its result, ignoring the non-int steps
    // entirely leaves the integer operand stack exactly as the source wrote it.

    private static readonly FieldInfo? _moveOnPerformField =
        typeof(MoveState).GetField("_onPerform", BindingFlags.NonPublic | BindingFlags.Instance);

    // (concrete monster type, move id) -> effects dict (null = nothing found)
    private static readonly Dictionary<(Type, string), Dictionary<string, object?>?> _moveFxCache = new();

    /// <summary>The combat the cached scans resolved their amounts against.</summary>
    private static object? _moveFxScope;

    internal static Dictionary<string, object?>? GetMoveEffects(MonsterModel monster, MoveState move)
    {
        try
        {
            // Amounts are read off the live monster, so a scan is only reusable
            // within the combat it was taken in: ascension-scaled stat getters
            // and the run's player count both differ between runs, and the
            // cache is keyed only by type and move id.  Dropping the table when
            // the combat changes serves fresh numbers instead of the previous
            // fight's.
            object? scope = monster.CombatState;
            if (!ReferenceEquals(scope, _moveFxScope))
            {
                _moveFxCache.Clear();
                _moveFxScope = scope;
            }
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
        /// <summary>Block granted to another creature on the mover's own side.</summary>
        internal readonly List<Dictionary<string, object?>> AllyBlock = new();
        internal int? Heal;
        /// <summary>Status/generated cards inserted, one entry per insert call.</summary>
        internal readonly List<Dictionary<string, object?>> StatusCards = new();

        /// <summary>Top-of-stack integer; null once arithmetic makes it unknown.</summary>
        internal int? LastInt;
        /// <summary>Integer/enum constants pushed for the call being built up.</summary>
        internal readonly List<int?> IntArgs = new();
        /// <summary>Constants belonging to the call currently being dispatched.</summary>
        internal List<int?> CallArgs = new();
        /// <summary>The evaluation stack, restricted to its integer entries.</summary>
        /// <remarks>
        /// Kept apart from <see cref="IntArgs"/>, which is an argument list: a
        /// getter that returns something other than an int ends the run of
        /// constants belonging to a call, but it does not disturb arithmetic
        /// around it, because its own receiver pop and result push cancel.
        /// Clearing one list for the other's benefit is what previously made
        /// `30 * chain.Count` unreadable.
        /// </remarks>
        internal readonly List<int?> Operands = new();

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
        /// Monster class named by the most recent lambda's type test, which is
        /// how a move says who it is about to shield.
        /// </summary>
        /// <remarks>
        /// Survives across statements, as <see cref="PendingCard"/> and
        /// <see cref="PendingPower"/> do: the `Where` that builds the recipient
        /// list and the `GainBlock` inside the following `foreach` are separate
        /// statements, so an argument run cannot be what carries it.
        /// </remarks>
        internal string? PendingRecipient;
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
        /// <summary>
        /// Whether the collection expression evaluated so far in this argument
        /// run came from `GetTeammatesOf` — the mover's own side.
        /// </summary>
        internal bool LastCollectionIsOwnSide;
        /// <summary>
        /// The above, sampled when the amount was converted, for the same
        /// reason <see cref="TargetWasSelf"/> is.
        /// </summary>
        internal bool TargetWasOwnSide;
        /// <summary>
        /// The object the argument expression evaluated so far has reached, so a
        /// getter declared on neither the monster nor a static type still has a
        /// receiver to be invoked against.
        /// </summary>
        internal object? LastObject;

        private int? _pendingDecimal;
        private bool _hasPendingDecimal;

        internal bool IsEmpty =>
            Applies.Count == 0 && Block == null && AllyBlock.Count == 0
            && Heal == null && StatusCards.Count == 0;

        internal void PushInt(int? value)
        {
            LastInt = value;
            IntArgs.Add(value);
            Operands.Add(value);
        }

        /// <summary>Forget the tracked int: an amount we cannot evaluate must
        /// report as unknown rather than as one of its operands.</summary>
        internal void PoisonInt()
        {
            LastInt = null;
            IntArgs.Clear();
            Operands.Clear();
        }

        internal void Negate()
        {
            if (IntArgs.Count > 0 && IntArgs[^1] == LastInt)
                IntArgs[^1] = -LastInt;
            if (Operands.Count > 0 && Operands[^1] == LastInt)
                Operands[^1] = -LastInt;
            LastInt = -LastInt;
        }

        /// <summary>
        /// Folds a binary integer operation over the two most recent pushes.
        /// </summary>
        /// <remarks>
        /// Exact for the straight-line arithmetic these amounts use
        /// (`BootUpStrGain * (2 - StockAmount)`, `30 * Players.Count`). Anything
        /// with an unknown operand, or with too few operands to be sure what is
        /// being combined, poisons instead — a computed amount must report as
        /// unknown, never as an operand.
        /// </remarks>
        internal void BinaryOp(Func<int, int, int?> combine)
        {
            if (Operands.Count < 2)
            {
                PoisonInt();
                return;
            }
            int? right = Operands[^1];
            int? left = Operands[^2];
            Operands.RemoveRange(Operands.Count - 2, 2);
            // The folded value replaces its operands in the argument list too,
            // when they are still the constants that list ends with.
            int consumed = Math.Min(2, IntArgs.Count);
            IntArgs.RemoveRange(IntArgs.Count - consumed, consumed);
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
            TargetWasOwnSide = LastCollectionIsOwnSide;
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
            Operands.Clear();
            // The next statement's arguments start fresh, so a loop body cannot
            // inherit the previous iteration's applier or receiver chain.
            LastCreatureIsSelf = false;
            LastCollectionIsOwnSide = false;
            LastObject = null;
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
        // Block the move puts on someone else. Kept out of "block" so a
        // consumer never credits it to the mover; a null "monster" means the
        // recipient could not be read, which is a known unknown rather than a
        // reason to guess.
        if (scan.AllyBlock.Count > 0) fx["ally_block"] = scan.AllyBlock;
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
            // A chain receiver lives only for the instruction that immediately
            // follows the getter which produced it: `a.b.c` compiles to
            // consecutive callvirts, so anything else reaching the stack ends
            // the chain rather than letting an unrelated `Count` resolve
            // against a collection left over from an earlier expression.
            if (op != 0x28 && op != 0x6F)
                scan.LastObject = null;
            if (op == 0xFE)
            {
                byte op2 = il[i + 1];
                // ldftn/ldvirtftn loads the lambda a following `Where` filters
                // with, and a filter that type-tests a monster class is how a
                // move names who it is about to shield.
                if ((op2 == 0x06 || op2 == 0x07) && i + 6 <= il.Length)
                {
                    try
                    {
                        var lambda = module.ResolveMethod(BitConverter.ToInt32(il, i + 2));
                        if (lambda != null)
                            scan.PendingRecipient = MonsterTypeTestedBy(lambda) ?? scan.PendingRecipient;
                    }
                    catch { }
                }
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
                    // Only an int field is an operand. Everything else loaded
                    // this way is a reference — an async state machine reaches
                    // its monster through `<>4__this`, and its own parameters
                    // through fields, before nearly every call — so pushing one
                    // as an unknown int both poisons arithmetic it was never
                    // part of and shifts the argument list that pile, count and
                    // position are read out of positionally. A reference is not
                    // an int-like argument, so it is skipped rather than
                    // recorded as an unknown one.
                    if (IsIntField(module, tok))
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

    /// <summary>
    /// The monster class a lambda type-tests, when it tests exactly one.
    /// </summary>
    /// <remarks>
    /// `Guardbot.GuardMove` does not shield itself — it shields
    /// `Enemies.Where(c => c.Monster is Fabricator)`. The predicate is a cached
    /// lambda the move body reaches through `ldftn`, and `x is T` lowers to a
    /// single `isinst`, so the recipient class is statically readable.
    ///
    /// Two guards keep an unrelated cast from being mistaken for a recipient:
    /// the tested type must be a `MonsterModel`, and there must be exactly one
    /// such test. A lambda that tests two says nothing, because which of them
    /// selects the recipient is not something a linear read can tell.
    /// </remarks>
    private static string? MonsterTypeTestedBy(MethodBase lambda)
    {
        byte[]? il;
        try { il = lambda.GetMethodBody()?.GetILAsByteArray(); }
        catch { return null; }
        if (il == null)
            return null;

        string? found = null;
        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            if ((op == 0x74 || op == 0x75) && i + 5 <= il.Length)   // castclass / isinst
            {
                try
                {
                    var tested = lambda.Module.ResolveType(BitConverter.ToInt32(il, i + 1));
                    if (tested != null && typeof(MonsterModel).IsAssignableFrom(tested))
                    {
                        if (found != null)
                            return null;
                        found = tested.Name;
                    }
                }
                catch { }
            }
            i += InstructionLength(il, i);
        }
        return found;
    }

    /// <summary>
    /// Powers of the attacker's own that an intent's damage calculation reads.
    /// </summary>
    /// <remarks>
    /// Almost every attack's damage is a constant or a stat getter, and the
    /// intent label the game renders already bakes the attacker's current
    /// Strength and Weak — which is why a consumer applies those as deltas from
    /// the values present when the label was read.
    ///
    /// `TheForgotten.DreadDamage` is `13 + Creature.GetPowerAmount&lt;DexterityPower&gt;()`,
    /// and its Miasma move steals 2 Dexterity from the player and stacks it on
    /// itself every other turn. So a Dexterity the *simulation* introduces
    /// raises the Dread too, and a consumer that only tracks Strength
    /// understates every predicted Dread by the amount stolen since.
    ///
    /// Reading this off the calc rather than naming the monster keeps it
    /// general: any attack that reads a power of its owner's is picked up. The
    /// walk follows one level of call into methods declared on a
    /// <c>MonsterModel</c>, because `() =&gt; DreadDamage` reaches the power
    /// through the monster's own property getter. Anything unreadable exports
    /// nothing, which leaves a consumer exactly as it was.
    /// </remarks>
    internal static List<string>? PowersScalingDamage(Delegate? damageCalc)
    {
        if (damageCalc?.Method == null)
            return null;
        var found = new List<string>();
        CollectPowerReads(damageCalc.Method, found, 1);
        return found.Count > 0 ? found : null;
    }

    private static void CollectPowerReads(MethodBase method, List<string> found, int depth)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return; }
        if (il == null)
            return;

        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            if ((op == 0x28 || op == 0x6F) && i + 5 <= il.Length)   // call / callvirt
            {
                try
                {
                    if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) is MethodInfo callee)
                    {
                        if (callee.Name == "GetPowerAmount" && callee.IsGenericMethod)
                        {
                            string? power = callee.GetGenericArguments().FirstOrDefault()?.Name;
                            if (power != null && !found.Contains(power))
                                found.Add(power);
                        }
                        else if (depth > 0 && callee.DeclaringType != null
                                 && typeof(MonsterModel).IsAssignableFrom(callee.DeclaringType))
                        {
                            CollectPowerReads(callee, found, depth - 1);
                        }
                    }
                }
                catch { }
            }
            i += InstructionLength(il, i);
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
        // (get_Creature, get_AttackSfx) must NOT clobber the tracked int, but
        // they do carry the chain forward so a getter further along still has a
        // receiver (`Creature.CombatState.Players.Count`).
        if (mm.Name.StartsWith("get_") && mm is MethodInfo getter)
        {
            // Which creature an expression produced decides who a non-generic
            // apply targets, since that overload's parameter is always Creature.
            if (getter.Name == "get_Creature")
                scan.LastCreatureIsSelf = true;
            // An indexer takes arguments off the stack this scan does not model,
            // so it resolves to nothing and merely ends the run of constants.
            object? value = getter.GetParameters().Length == 0
                ? InvokeGetter(getter, ReceiverFor(getter, monster, scan))
                : null;
            if (getter.ReturnType == typeof(int) && getter.GetParameters().Length == 0)
            {
                scan.PushInt(value as int?);
            }
            else
            {
                scan.IntArgs.Clear();
                scan.LastObject = value;
            }
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
            if (applyMi.IsGenericMethod)
            {
                string? powerName = applyMi.GetGenericArguments().FirstOrDefault()?.Name;
                bool collection = pars.Length > 1
                    && typeof(IEnumerable).IsAssignableFrom(pars[1].ParameterType)
                    && pars[1].ParameterType != typeof(string);
                // Who receives it comes from the argument expression, not from
                // the parameter type. A collection is the player side unless it
                // came from `GetTeammatesOf`; a single Creature is the monster
                // itself only when it came from its own `Creature`. Everything
                // else — a loop variable over the move's targets, a teammate
                // picked out of a filtered list — is an honest unknown.
                string? side = collection
                    ? (scan.TargetWasOwnSide ? null : "player")
                    : (scan.TargetWasSelf ? "self" : null);
                scan.Applies.Add(new Dictionary<string, object?>
                {
                    ["power"] = powerName,
                    ["target"] = side ?? "unknown",
                    ["amount"] = scan.TakeAmount(),
                });
                return;
            }
            {
                // Apply(ctx, PowerModel power, Creature target, ...) applies an
                // instance built at runtime. Its target parameter is a single
                // Creature either way, so who receives it is read from the
                // expression that produced that creature: the monster's own
                // `Creature` property means self, anything else (a loop
                // variable over the move's targets) means the player side.
                // Every shipped use of this overload does aim at the players.
                string? powerName = scan.PendingPower;
                bool playerSide = !scan.TargetWasSelf;
                scan.PendingPower = null;
                if (powerName == null)
                {
                    scan.TakeAmount();
                    return;
                }
                scan.Applies.Add(new Dictionary<string, object?>
                {
                    ["power"] = powerName,
                    ["target"] = playerSide ? "player" : "self",
                    ["amount"] = scan.TakeAmount(),
                });
            }
        }
        else if (owner == "CreatureCmd" && mm.Name == "GainBlock")
        {
            // The amount is consumed either way, so it can never be inherited
            // by the next effect. Who receives it is read from the creature
            // expression, sampled at the amount conversion — the same rule the
            // non-generic apply above uses, and the same reason: the argument
            // that follows would otherwise overwrite the flag.
            int? gained = scan.TakeAmount();
            if (gained is int amount && amount != 0)
            {
                if (scan.TargetWasSelf)
                {
                    scan.Block = (scan.Block ?? 0) + amount;
                }
                else
                {
                    scan.AllyBlock.Add(new Dictionary<string, object?>
                    {
                        ["amount"] = amount,
                        ["monster"] = scan.PendingRecipient,
                    });
                }
            }
            scan.PendingRecipient = null;
        }
        else if (mm.Name == "GetTeammatesOf")
        {
            // `CombatState.GetTeammatesOf(c)` is the mover's OWN side, owner
            // included. Set after `EndArgumentRun` above so it survives into
            // the apply that consumes the collection, and cleared by that
            // apply's own run — the same lifetime `LastCreatureIsSelf` has.
            scan.LastCollectionIsOwnSide = true;
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
    /// <summary>Whether a field token names an <c>int</c> field.</summary>
    private static bool IsIntField(Module module, int token)
    {
        try
        {
            return module.ResolveField(token)?.FieldType == typeof(int);
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>
    /// The object a zero-argument getter is invoked against.
    /// </summary>
    /// <remarks>
    /// The monster's own design properties come first, so a chain can always
    /// restart from `base.Creature` however deep the previous one went.
    /// Otherwise the getter is offered the object the chain has reached, which
    /// is what lets `ICombatState.Players` and `IReadOnlyList&lt;Player&gt;.Count`
    /// resolve — both are declared on interfaces the reached object implements.
    /// A getter that matches neither has no receiver and stays unknown.
    /// </remarks>
    private static object? ReceiverFor(MethodInfo getter, MonsterModel monster, MoveFxScan scan)
    {
        if (getter.IsStatic)
            return null;
        if (getter.DeclaringType!.IsInstanceOfType(monster))
            return monster;
        if (scan.LastObject != null && getter.DeclaringType.IsInstanceOfType(scan.LastObject))
            return scan.LastObject;
        return null;
    }

    private static object? InvokeGetter(MethodInfo getter, object? receiver)
    {
        try
        {
            if (getter.IsStatic || receiver != null)
                return getter.Invoke(receiver, null);
        }
        catch { }
        return null;
    }
}
