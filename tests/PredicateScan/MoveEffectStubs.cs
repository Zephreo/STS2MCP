using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// Stand-ins for the game's command surface. The move-effect scanner matches on
// declaring-type and method NAMES, on parameter types (Creature vs IEnumerable,
// decimal vs int), and on generic arguments — so these reproduce those exactly
// while the bodies do nothing. Namespaces are irrelevant to the scanner.

public class CardModel { }
public class Player { }
public class PowerModel { }
public class CardPlay { }
public enum ValueProp { Move }
public enum PileType { None, Draw, Hand, Discard, Exhaust }
public enum CardPilePosition { None, Bottom, Top, Random }

public class StrengthPower : PowerModel { }
public class WeakPower : PowerModel { }
public class FrailPower : PowerModel { }
public class IntangiblePower : PowerModel { }
public class SandpitPower : PowerModel { }
public class DampenPower : PowerModel { }
public class Dazed : CardModel { }
public class Slimed : CardModel { }
public class Beckon : CardModel { }

public class PlayerChoiceContext { }
public class ThrowingPlayerChoiceContext : PlayerChoiceContext { }

public static class PowerCmd
{
    public static Task<T?> Apply<T>(PlayerChoiceContext ctx, Creature target, decimal amount,
        Creature? applier, CardModel? source, bool silent = false) where T : PowerModel =>
        Task.FromResult<T?>(null);

    public static Task<IReadOnlyList<T>> Apply<T>(PlayerChoiceContext ctx, IEnumerable<Creature> targets,
        decimal amount, Creature? applier, CardModel? source, bool silent = false) where T : PowerModel =>
        Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

    public static Task Apply(PlayerChoiceContext ctx, PowerModel power, Creature target, decimal amount,
        Creature? applier, CardModel? source, bool silent = false) => Task.CompletedTask;
}

public static class CreatureCmd
{
    public static Task<decimal> GainBlock(Creature creature, decimal amount, ValueProp props,
        CardPlay? play, bool fast = false) => Task.FromResult(amount);

    public static Task Heal(Creature creature, decimal amount, bool playAnim = true) => Task.CompletedTask;

    public static Task TriggerAnim(Creature creature, string anim, float time) => Task.CompletedTask;
}

public static class CardPileCmd
{
    public static Task AddToCombatAndPreview<T>(IEnumerable<Creature> targets, PileType pileType, int count,
        Player? creator, CardPilePosition position = CardPilePosition.Bottom) where T : CardModel =>
        Task.CompletedTask;

    public static Task<int> AddGeneratedCardToCombat(CardModel card, PileType newPileType, Player? creator,
        CardPilePosition position = CardPilePosition.Bottom) => Task.FromResult(0);
}

public static class ModelDb
{
    public static T Power<T>() where T : PowerModel, new() => new T();
}

public static class DamageCmd
{
    public static AttackCommand Attack(decimal damagePerHit) => new AttackCommand();
}

public sealed class AttackCommand
{
    public AttackCommand FromMonster(object monster) => this;
    public AttackCommand WithHitCount(int hitCount) => this;
    public AttackCommand WithAttackerAnim(string anim, float time) => this;
    public Task Execute(object? target) => Task.CompletedTask;
}

public static class MutableExtensions
{
    public static T ToMutable<T>(this T model) => model;
}

namespace MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine
{
    /// <summary>Holds the move body in the private field the scanner reflects.</summary>
    public sealed class MoveState
    {
        private readonly Delegate? _onPerform;
        public MoveState(Delegate onPerform) => _onPerform = onPerform;
        public string Id => "TEST_MOVE";
    }
}
