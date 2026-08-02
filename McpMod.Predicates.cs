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
}
