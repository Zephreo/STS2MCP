using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace STS2_MCP;

public static partial class McpMod
{
    // Intent-predicate translation.
    //
    // A ConditionalBranchState branch holds a `Func<bool>` closure over the
    // live monster, and a RandomBranchState branch holds a `Func<float>`
    // weight. Neither can be re-evaluated against a hypothetical future state,
    // so the export has to describe what the closure TESTS rather than what it
    // currently returns.
    //
    // Reading the closure's IL recovers three things straight from the shipped
    // game code: which member the branch reads, which comparison it makes, and
    // the constant it compares against. Only the meaning of a member -- that
    // `TestSubject.Respawns` counts RESPAWN_MOVE performances, say -- has to be
    // stated here, because it lives in a different method than the predicate.
    // That mapping is keyed on the member itself, so it survives a monster
    // being renamed, its branch ids changing, or its branch targets being
    // reordered; and every threshold still comes from the game's own IL.

    /// <summary>What a predicate's IL was seen to read and compare.</summary>
    private sealed class PredicateScan
    {
        /// <summary>Members read, as `DeclaringType.Name`, in IL order.</summary>
        internal readonly List<string> Members = new();
        /// <summary>Methods called, in IL order, for return-type inspection.</summary>
        internal readonly List<MethodBase> Calls = new();
        /// <summary>Generic argument names of any generic call, in IL order.</summary>
        internal readonly List<string> GenericArgs = new();
        /// <summary>Methods reached through `ldftn` (LINQ predicate lambdas).</summary>
        internal readonly List<MethodBase> FunctionPointers = new();
        /// <summary>Integer constants pushed, in IL order.</summary>
        internal readonly List<int> Ints = new();
        /// <summary>String literals pushed, in IL order.</summary>
        internal readonly List<string> Strings = new();
        /// <summary>Comparison opcodes, as `ceq` / `cgt` / `clt`, in IL order.</summary>
        internal readonly List<string> Comparisons = new();
        /// <summary>Whether the body divides (an integer HP fraction).</summary>
        internal bool HasDivision;
        /// <summary>Whether the body branches (a `||`, `&amp;&amp;` or ternary).</summary>
        internal bool HasBranch;

        internal bool Reads(string member) => Members.Contains(member);
    }

    /// <summary>Whether the scan read a member ending in any of the suffixes.</summary>
    /// <remarks>
    /// Matching on the member name alone keeps a check independent of which
    /// monster class happens to declare it.
    /// </remarks>
    private static bool ReadsAny(PredicateScan scan, params string[] suffixes) =>
        scan.Members.Any(member => suffixes.Any(member.EndsWith));

    /// <summary>
    /// Scans keyed by method. A method body never changes, and the intent
    /// machine is rebuilt on every state poll, so without this the same
    /// predicates would be re-decoded several times a second.
    /// </summary>
    private static readonly Dictionary<MethodBase, PredicateScan> _predicateScanCache = new();

    /// <summary>Reads one method body into a <see cref="PredicateScan"/>.</summary>
    /// <remarks>
    /// Anything unrecognized is simply not recorded, which downgrades the
    /// branch to a snapshot rather than producing a wrong condition.
    /// </remarks>
    private static PredicateScan ScanPredicate(MethodBase? method)
    {
        if (method != null && _predicateScanCache.TryGetValue(method, out var cached))
            return cached;
        var scan = ScanPredicateUncached(method);
        if (method != null)
            _predicateScanCache[method] = scan;
        return scan;
    }

    private static PredicateScan ScanPredicateUncached(MethodBase? method)
    {
        var scan = new PredicateScan();
        byte[]? il = null;
        try { il = method?.GetMethodBody()?.GetILAsByteArray(); }
        catch { }
        if (il == null || method == null)
            return scan;
        var module = method.Module;

        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i];
            int length = InstructionLength(il, i);
            if (op == 0xFE && i + 1 < il.Length)
            {
                switch (il[i + 1])
                {
                    case 0x01: scan.Comparisons.Add("ceq"); break;
                    case 0x02: case 0x03: scan.Comparisons.Add("cgt"); break;
                    case 0x04: case 0x05: scan.Comparisons.Add("clt"); break;
                    case 0x06: case 0x07:   // ldftn / ldvirtftn
                        AddFunctionPointer(module, il, i + 2, scan);
                        break;
                }
                i += length;
                continue;
            }
            switch (op)
            {
                case >= 0x16 and <= 0x1E: scan.Ints.Add(op - 0x16); break;  // ldc.i4.0 .. 8
                case 0x15: scan.Ints.Add(-1); break;                        // ldc.i4.m1
                case 0x1F: scan.Ints.Add((sbyte)il[i + 1]); break;          // ldc.i4.s
                case 0x20: scan.Ints.Add(BitConverter.ToInt32(il, i + 1)); break;
                case 0x72: AddString(module, il, i + 1, scan); break;             // ldstr
                case 0x5B or 0x5C: scan.HasDivision = true; break;          // div / div.un
                case (>= 0x2B and <= 0x37) or (>= 0x38 and <= 0x44):
                    // br.s/br are the ternary's own join, not a decision.
                    if (op != 0x2B && op != 0x38)
                        scan.HasBranch = true;
                    break;
                case 0x28 or 0x6F: AddMember(module, il, i + 1, scan, isField: false); break;
                case 0x7B or 0x7E: AddMember(module, il, i + 1, scan, isField: true); break;
            }
            i += length;
        }
        return scan;
    }

    private static void AddMember(Module module, byte[] il, int tokenOffset, PredicateScan scan, bool isField)
    {
        if (tokenOffset + 4 > il.Length)
            return;
        int token = BitConverter.ToInt32(il, tokenOffset);
        try
        {
            if (isField)
            {
                var field = module.ResolveField(token);
                if (field?.DeclaringType != null && !IsCompilerGenerated(field.DeclaringType))
                    scan.Members.Add($"{field.DeclaringType.Name}.{field.Name}");
                return;
            }
            var method = module.ResolveMethod(token);
            if (method?.DeclaringType == null)
                return;
            scan.Members.Add($"{method.DeclaringType.Name}.{method.Name}");
            scan.Calls.Add(method);
            if (method.IsGenericMethod)
            {
                foreach (var arg in method.GetGenericArguments())
                    scan.GenericArgs.Add(arg.Name);
            }
        }
        catch { }
    }

    /// <summary>Resolves an `ldstr` token into the literal it pushes.</summary>
    private static void AddString(Module module, byte[] il, int tokenOffset, PredicateScan scan)
    {
        if (tokenOffset + 4 > il.Length)
            return;
        try
        {
            var literal = module.ResolveString(BitConverter.ToInt32(il, tokenOffset));
            if (literal != null)
                scan.Strings.Add(literal);
        }
        catch { }
    }

    private static void AddFunctionPointer(Module module, byte[] il, int tokenOffset, PredicateScan scan)
    {
        if (tokenOffset + 4 > il.Length)
            return;
        try
        {
            var target = module.ResolveMethod(BitConverter.ToInt32(il, tokenOffset));
            if (target != null)
                scan.FunctionPointers.Add(target);
        }
        catch { }
    }

    /// <summary>Closure display classes are noise, not tested members.</summary>
    private static bool IsCompilerGenerated(Type type) =>
        type.Name.StartsWith("<") || type.Name.Contains("__DisplayClass");

    /// <summary>
    /// The comparison the predicate makes, normalized against its constant.
    /// </summary>
    /// <remarks>
    /// C# lowers `a &gt;= k` to `a &lt; k` followed by a boolean negation, so the
    /// raw opcode alone is not the source comparison. `negated` folds the
    /// trailing `ldc.i4.0; ceq` back in.
    /// </remarks>
    private static string? SourceComparison(PredicateScan scan, bool negated)
    {
        string? raw = scan.Comparisons.FirstOrDefault(c => c != "ceq") ?? scan.Comparisons.FirstOrDefault();
        return raw switch
        {
            "clt" => negated ? "ge" : "lt",
            "cgt" => negated ? "le" : "gt",
            "ceq" => negated ? "ne" : "eq",
            _ => null,
        };
    }

    /// <summary>The threshold the predicate compares against.</summary>
    /// <remarks>
    /// The negation `a &gt;= k` lowers to `clt` against the same `k` plus
    /// `ldc.i4.0; ceq`, so the trailing zero is dropped before taking the last
    /// constant.
    /// </remarks>
    private static int? Threshold(PredicateScan scan)
    {
        var ints = new List<int>(scan.Ints);
        if (scan.Comparisons.Count > 1 && ints.Count > 1 && ints[^1] == 0)
            ints.RemoveAt(ints.Count - 1);
        return ints.Count > 0 ? ints[^1] : null;
    }

    /// <summary>Whether the predicate's final result is inverted.</summary>
    /// <remarks>
    /// `!x` and the `&gt;=` / `&lt;=` lowerings all end in `ldc.i4.0; ceq`. A lone
    /// trailing `ceq` is ambiguous: it is C#'s `!` over a bool, but it is the
    /// source's own `== 0` over an int (`GetAllyCount() == 0`). The value's type
    /// separates the two.
    /// </remarks>
    private static bool IsNegated(PredicateScan scan)
    {
        if (scan.Comparisons.Count == 0 || scan.Comparisons[^1] != "ceq")
            return false;
        if (scan.Comparisons.Count == 1 && !TestsBoolean(scan))
            return false;
        return scan.Ints.Count > 0 && scan.Ints[^1] == 0;
    }

    /// <summary>Whether the value the predicate tests is a boolean.</summary>
    private static bool TestsBoolean(PredicateScan scan) =>
        scan.Calls.Any(call => call is MethodInfo method && method.ReturnType == typeof(bool));

    /// <summary>
    /// Scans the monster member a predicate delegates to, if it has one.
    /// </summary>
    /// <remarks>
    /// `() =&gt; CanFabricate` says nothing about a threshold — the comparison and
    /// its constant live in the property body. One level of inlining recovers
    /// them from the source instead of restating them here.
    /// </remarks>
    private static PredicateScan? InlineMonsterCall(PredicateScan outer)
    {
        foreach (var call in outer.Calls)
        {
            var declaring = call.DeclaringType;
            if (declaring == null || !typeof(MegaCrit.Sts2.Core.Models.MonsterModel).IsAssignableFrom(declaring))
                continue;
            if (call.Name == "get_Creature")
                continue;
            var inner = ScanPredicate(call);
            if (inner.Comparisons.Count > 0 || inner.FunctionPointers.Count > 0)
                return inner;
        }
        return null;
    }

    /// <summary>
    /// Whether a teammate-count expression excludes the counting monster.
    /// </summary>
    /// <remarks>
    /// `CombatState.GetTeammatesOf` returns everyone on the creature's side,
    /// the owner included, so a count is self-inclusive unless its LINQ
    /// predicate rejects the owner with a `!=` against it. Reading that from
    /// the lambda keeps the two shapes apart without naming either monster.
    /// </remarks>
    private static bool CountExcludesSelf(params PredicateScan?[] scans)
    {
        foreach (var scan in scans)
        {
            if (scan == null)
                continue;
            foreach (var pointer in scan.FunctionPointers)
            {
                var lambda = ScanPredicate(pointer);
                // `Creature` declares no equality operators, so `c != owner` is
                // a reference compare: a `ceq` against the captured owner.
                bool readsOwner = lambda.Members.Any(member => member.EndsWith(".get_Creature"));
                if (readsOwner && lambda.Comparisons.Contains("ceq"))
                    return true;
                if (lambda.Members.Any(member => member.EndsWith(".op_Inequality")))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a weight lambda can change during combat.
    /// </summary>
    /// <remarks>
    /// `AddBranch(state, repeatType, float weight)` wraps a constant in
    /// `() =&gt; weight`, whose body only loads a captured field. A weight that
    /// calls anything (Two-Tailed Rat consults `CanSummon`) is live state, and
    /// the single sample taken at export time cannot stand in for it.
    /// </remarks>
    private static bool IsDynamicWeight(Delegate? weightLambda)
    {
        if (weightLambda == null)
            return false;
        var scan = ScanPredicate(weightLambda.Method);
        return scan.Members.Count > 0 || scan.HasBranch;
    }

    /// <summary>Names the supported live rule behind a random weight.</summary>
    private static string? DynamicWeightRule(Delegate? weightLambda)
    {
        if (weightLambda == null)
            return null;
        var scan = ScanPredicate(weightLambda.Method);
        return ReadsAny(scan, ".CanSummon") ? "two_tailed_rat_can_summon" : null;
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
    /// <param name="placementSampled">
    /// Whether <paramref name="enabled"/> was measured against a live creature.
    /// The mod samples one, so an encounter-placement test resolves to the
    /// constant it evaluated to; a creature-less host (the offline model export)
    /// has nothing to sample and needs the test itself.
    /// </param>
    internal static Dictionary<string, object?> BuildCondition(
        Delegate? lambda, int branchIndex, bool enabled, out bool isSnapshot, bool placementSampled = true)
    {
        isSnapshot = false;
        var scan = ScanPredicate(lambda?.Method);
        bool negated = IsNegated(scan);

        // Encounter-fixed placement: the slot a creature occupies, and the
        // `IsFront` / `IsAlone` flags the encounter sets before combat, never
        // change once the monster exists. The sampled value is therefore exact
        // for the whole search rather than a snapshot of moving state.
        if (ReadsAny(scan, ".get_SlotName", ".get_IsFront", ".get_IsAlone"))
        {
            if (placementSampled)
                return Condition("constant", ("value", enabled));
            var placement = PlacementCondition(scan, negated);
            if (placement != null)
                return placement;
            isSnapshot = true;
            return Condition("snapshot", ("value", enabled));
        }

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

    /// <summary>
    /// The encounter-placement test a predicate makes, for a host that cannot
    /// sample it.
    /// </summary>
    /// <remarks>
    /// Five monsters open on `Creature.SlotName == "<c>first</c>"` and its
    /// siblings, where the literal comes straight out of the predicate's own
    /// `ldstr`. Toadpole and Nibbit instead read `IsFront` / `IsAlone`, which
    /// the encounter stamps on the monster instance it generates rather than on
    /// the slot, so those travel as flags of their own.
    ///
    /// Returns null for a placement test in none of those shapes, which the
    /// caller downgrades to a snapshot rather than guessing.
    /// </remarks>
    private static Dictionary<string, object?>? PlacementCondition(PredicateScan scan, bool negated)
    {
        if (ReadsAny(scan, ".get_SlotName"))
        {
            string? slot = scan.Strings.LastOrDefault();
            return slot == null ? null : Negate(Condition("owner_slot", ("name", slot)), negated);
        }
        if (ReadsAny(scan, ".get_IsFront"))
            return Negate(Condition("owner_is_front"), negated);
        if (ReadsAny(scan, ".get_IsAlone"))
            return Negate(Condition("owner_is_alone"), negated);
        return null;
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
