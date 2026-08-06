using System;
using System.Collections.Generic;
using System.Linq;

// Stand-ins for the game types the scanner names, so the real scanner sources
// compile without the game assemblies.
namespace MegaCrit.Sts2.Core.Models
{
    public class MonsterModel
    {
        public Creature Creature { get; } = new Creature();
        public CombatStateStub CombatState { get; } = new CombatStateStub();
    }
}

public class Creature
{
    public bool IsAlive => true;
    public int CurrentHp => 189;
    public int MaxHp => 379;
    public string SlotName => "first";
    public bool HasPower<T>() => false;

    /// <summary>What `TheForgotten.DreadDamage` reads to scale its attack.</summary>
    public int GetPowerAmount<T>() => 0;
    public CombatState CombatState { get; } = new CombatState();

    /// <summary>What a `Where(c =&gt; c.Monster is Fabricator)` filter tests.</summary>
    public MegaCrit.Sts2.Core.Models.MonsterModel? Monster => null;
}

public class CombatState
{
    public IReadOnlyList<Creature> GetTeammatesOf(Creature creature) => Array.Empty<Creature>();

    /// <summary>The mover's own side, which is what `Guardbot.GuardMove` filters.</summary>
    public IReadOnlyList<Creature> Enemies { get; } = Array.Empty<Creature>();

    /// <summary>Two players, so a per-player amount differs from its multiplier.</summary>
    public IReadOnlyList<Player> Players { get; } = new[] { new Player(), new Player() };
}

public class AsleepPower { }

// Each fake reproduces one shipped predicate shape verbatim. The class names
// are only labels; the scanner keys on the members, not on them.

/// <summary>`TestSubject`: an int counter a move increments.</summary>
public sealed class CounterProperty : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private int _respawns;
    private int Respawns { get { return _respawns; } set { _respawns = value; } }
    public Func<bool> Below() => () => Respawns < 2;
    public Func<bool> AtLeast() => () => Respawns >= 2;
}

/// <summary>`KnowledgeDemon`: the same, read straight off the field.</summary>
public sealed class CounterField : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private int _curseOfKnowledgeCounter;
    public Func<bool> Below() => () => _curseOfKnowledgeCounter < 3;
    public Func<bool> AtLeast() => () => _curseOfKnowledgeCounter >= 3;
    public void Bump() => _curseOfKnowledgeCounter++;
}

/// <summary>`BowlbugRock`: a plain mutable bool.</summary>
public sealed class BoolFlag : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private bool _isOffBalance;
    public bool IsOffBalance { get { return _isOffBalance; } set { _isOffBalance = value; } }
    public Func<bool> Set() => () => IsOffBalance;
    public Func<bool> Clear() => () => !IsOffBalance;
}

/// <summary>`LagavulinMatriarch` / `SlumberingBeetle`: a power check.</summary>
public sealed class PowerCheck : MegaCrit.Sts2.Core.Models.MonsterModel
{
    public Func<bool> Has() => () => base.Creature.HasPower<AsleepPower>();
    public Func<bool> Lacks() => () => !base.Creature.HasPower<AsleepPower>();
}

/// <summary>`LivingShield`: a self-EXCLUSIVE living-teammate count.</summary>
public sealed class AllyCountExcludingSelf : MegaCrit.Sts2.Core.Models.MonsterModel
{
    public Func<bool> Any() => () => GetAllyCount() > 0;
    public Func<bool> None() => () => GetAllyCount() == 0;
    private int GetAllyCount() =>
        base.Creature.CombatState.GetTeammatesOf(base.Creature)
            .Count((Creature c) => c.IsAlive && c != base.Creature);
}

/// <summary>`Fabricator`: a self-INCLUSIVE count behind a bool property.</summary>
public sealed class AllyCountIncludingSelf : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private bool CanFabricate =>
        base.Creature.CombatState.GetTeammatesOf(base.Creature).Count((Creature c) => c.IsAlive) < 4;
    public Func<bool> Can() => () => CanFabricate;
    public Func<bool> Cannot() => () => !CanFabricate;
}

/// <summary>`Ovicopter`: the same, with an already-negated inner comparison.</summary>
public sealed class AllyCountAtMost : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private bool CanLay =>
        base.Creature.CombatState.GetTeammatesOf(base.Creature).Count((Creature c) => c.IsAlive) <= 3;
    public Func<bool> Can() => () => CanLay;
    public Func<bool> Cannot() => () => !CanLay;
}

/// <summary>`FrogKnight`: a short-circuit over a flag and an HP fraction.</summary>
public sealed class FlagOrHalfHealth : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private bool _hasBeetleCharged;
    private bool HasBeetleCharged { get { return _hasBeetleCharged; } set { _hasBeetleCharged = value; } }
    public Func<bool> Either() => () => HasBeetleCharged || base.Creature.CurrentHp >= base.Creature.MaxHp / 2;
    public Func<bool> Neither() => () => !HasBeetleCharged && base.Creature.CurrentHp < base.Creature.MaxHp / 2;
}

/// <summary>`Exoskeleton` / `Nibbit` / `Toadpole`: encounter-fixed placement.</summary>
public sealed class Placement : MegaCrit.Sts2.Core.Models.MonsterModel
{
    public bool IsFront { get; set; }
    public bool IsAlone { get; set; }
    public Func<bool> BySlot() => () => base.Creature.SlotName == "first";
    public Func<bool> ByFront() => () => ((Placement)(object)this).IsFront;
    public Func<bool> ByAlone() => () => ((Placement)(object)this).IsAlone;
}

/// <summary>`TwoTailedRat` versus every constant-weight branch.</summary>
public sealed class Weights
{
    private bool CanSummon() => false;
    public Func<float> Constant(float weight) => () => weight;
    public Func<float> Live() => () => (!CanSummon()) ? 1f : (1f / 12f);
}

namespace STS2_MCP
{
    public static partial class McpMod
    {
        private static int _failures;

        private static void Check(string label, object? actual, object? expected)
        {
            bool ok = string.Equals($"{actual}", $"{expected}", StringComparison.Ordinal);
            if (!ok)
                _failures++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {label}: got [{actual}] want [{expected}]");
        }

        /// <summary>Mirrors how BuildCondition picks the comparison source.</summary>
        private static void CheckComparison(string label, Delegate lambda, string cmp, string value)
        {
            var scan = ScanPredicate(lambda.Method);
            bool negated = IsNegated(scan);
            var inlined = InlineMonsterCall(scan);
            bool comparesDirectly = scan.Comparisons.Any(comparison => comparison != "ceq")
                || (scan.Comparisons.Count > 0 && !TestsBoolean(scan));
            var source = comparesDirectly || inlined == null ? scan : inlined;
            bool effective = ReferenceEquals(source, scan) ? negated : IsNegated(source) ^ negated;
            Check($"{label} cmp", SourceComparison(source, effective), cmp);
            Check($"{label} value", Threshold(source), value);
        }

        private static void CheckSelfInclusion(string label, Delegate lambda, bool excludesSelf)
        {
            var scan = ScanPredicate(lambda.Method);
            Check($"{label} excludes self", CountExcludesSelf(scan, InlineMonsterCall(scan)), excludesSelf);
        }

        public static int Main()
        {
            var counterProperty = new CounterProperty();
            CheckComparison("counter property <2", counterProperty.Below(), "lt", "2");
            CheckComparison("counter property >=2", counterProperty.AtLeast(), "ge", "2");

            var counterField = new CounterField();
            CheckComparison("counter field <3", counterField.Below(), "lt", "3");
            CheckComparison("counter field >=3", counterField.AtLeast(), "ge", "3");

            var flag = new BoolFlag();
            Check("bool flag negated", IsNegated(ScanPredicate(flag.Set().Method)), false);
            Check("negated bool flag negated", IsNegated(ScanPredicate(flag.Clear().Method)), true);

            var power = new PowerCheck();
            Check("power check negated", IsNegated(ScanPredicate(power.Has().Method)), false);
            Check("negated power check negated", IsNegated(ScanPredicate(power.Lacks().Method)), true);
            Check("power class", string.Join(",", ScanPredicate(power.Has().Method).GenericArgs), "AsleepPower");

            // The discriminator that matters: a lone trailing `ceq` is `!` over a
            // bool, but `== 0` over an int. Reading it the wrong way inverts the
            // branch.
            var exclusive = new AllyCountExcludingSelf();
            CheckComparison("ally count >0", exclusive.Any(), "gt", "0");
            CheckComparison("ally count ==0", exclusive.None(), "eq", "0");
            Check("int compare is not a negation", IsNegated(ScanPredicate(exclusive.None().Method)), false);
            CheckSelfInclusion("ally count", exclusive.Any(), excludesSelf: true);

            // The threshold lives in the property body, one call deeper.
            var inclusive = new AllyCountIncludingSelf();
            CheckComparison("inclusive count", inclusive.Can(), "lt", "4");
            CheckComparison("inclusive count negated", inclusive.Cannot(), "ge", "4");
            CheckSelfInclusion("inclusive count", inclusive.Can(), excludesSelf: false);

            // An inner `<=` is itself a negated `>`; the outer `!` composes with it.
            var atMost = new AllyCountAtMost();
            CheckComparison("at-most count", atMost.Can(), "le", "3");
            CheckComparison("at-most count negated", atMost.Cannot(), "gt", "3");

            var halfHealth = new FlagOrHalfHealth();
            var either = ScanPredicate(halfHealth.Either().Method);
            var neither = ScanPredicate(halfHealth.Neither().Method);
            Check("short circuit reads flag", either.Members.Any(m => m.EndsWith(".get_HasBeetleCharged")), true);
            Check("short circuit denominator", either.Ints.FirstOrDefault(value => value > 1), 2);
            Check("complement reads flag", neither.Members.Any(m => m.EndsWith(".get_HasBeetleCharged")), true);
            Check("complement denominator", neither.Ints.FirstOrDefault(value => value > 1), 2);

            var placement = new Placement();
            Check("slot is placement", ReadsAny(ScanPredicate(placement.BySlot().Method), ".get_SlotName"), true);
            Check("front is placement", ReadsAny(ScanPredicate(placement.ByFront().Method), ".get_IsFront"), true);
            Check("alone is placement", ReadsAny(ScanPredicate(placement.ByAlone().Method), ".get_IsAlone"), true);

            var weights = new Weights();
            Check("constant weight is static", IsDynamicWeight(weights.Constant(0.75f)), false);
            Check("live weight is dynamic", IsDynamicWeight(weights.Live()), true);

            CheckMoveEffects();

            Console.WriteLine(_failures == 0 ? "\nALL OK" : $"\n{_failures} FAILURES");
            return _failures == 0 ? 0 : 1;
        }
    }
}
