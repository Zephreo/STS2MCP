using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using STS2_MCP;

internal static class Program
{
    private static void Equal<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static CardModel Card() => new CardModel(new DynamicVar(5, 7, 16), new DynamicVar(9, 11, 20));

    private static void Restored(CardModel card)
    {
        Equal(card.DynamicVars.Values[0].EnchantedValue, 7m, "first enchanted value restored");
        Equal(card.DynamicVars.Values[0].PreviewValue, 16m, "first powered UI value restored");
        Equal(card.DynamicVars.Values[1].EnchantedValue, 11m, "second enchanted value restored");
        Equal(card.DynamicVars.Values[1].PreviewValue, 20m, "second powered UI value restored");
        if (card.Enchantment != null)
        {
            Equal(card.Enchantment.DynamicVars.Values[0].EnchantedValue, 4m, "enchantment enchanted value restored");
            Equal(card.Enchantment.DynamicVars.Values[0].PreviewValue, 10m, "enchantment powered UI value restored");
        }
    }

    private static void Refuses(CardModel card, bool swallowInner = false)
    {
        Exception? error = null;
        try
        {
            McpMod.TestBuild(true, () =>
            {
                if (!swallowInner) return McpMod.TestDescription(card);
                try { return McpMod.TestDescription(card); }
                catch { return "partial state from a best-effort DTO builder"; }
            });
        }
        catch (Exception caught) { error = caught; }
        Restored(card);
        Equal(McpMod.TestValueMode, false, "request value mode restored on failure");
        Equal(card.FallbackReads, 0, "unpowered request never reads powered fallback description");
        if (error == null) throw new InvalidOperationException("an invalid unpowered state was published");
        if (!error.ToString().Contains("injected")) throw new InvalidOperationException("the original preview failure was lost");
    }

    private static int Main()
    {
        var cases = new (string, Action)[]
        {
            ("successful preview restores UI values", () =>
            {
                var card = Card();
                Equal(McpMod.TestBuild(true, () => McpMod.TestDescription(card)), "7,11", "unpowered intrinsic values rendered");
                Restored(card);
                Equal(McpMod.TestValueMode, false, "value mode restored");
            }),
            ("partial card preview failure refuses state", () =>
            {
                var card = Card(); card.DynamicVars.Values[1].ThrowPreview = true; Refuses(card);
            }),
            ("enchantment preview failure restores both sets", () =>
            {
                var card = Card(); card.Enchantment = new EnchantmentModel(new DynamicVar(2, 4, 10) { ThrowPreview = true }); Refuses(card);
            }),
            ("formatting failure has no powered fallback", () =>
            {
                var card = Card(); card.ThrowDescription = true; Refuses(card);
            }),
            ("DTO catch cannot publish an invalid state", () =>
            {
                var card = Card(); card.DynamicVars.Values[1].ThrowPreview = true; Refuses(card, true);
            }),
            ("later request has no stale failure", () =>
            {
                var broken = Card(); broken.DynamicVars.Values[1].ThrowPreview = true; Refuses(broken);
                var card = Card();
                Equal(McpMod.TestBuild(true, () => McpMod.TestDescription(card)), "7,11", "next request renders normally");
                Restored(card);
            }),
            ("powered request keeps existing formatting fallback", () =>
            {
                var card = Card(); card.ThrowDescription = true;
                Equal(McpMod.TestBuild(false, () => McpMod.TestDescription(card)), "powered fallback", "powered display fallback");
                Equal(card.FallbackReads, 1, "powered fallback read once"); Restored(card);
            }),
            ("nested render scopes restore their immediate parent values", () =>
            {
                var card = Card();
                card.DuringDescription = () =>
                {
                    card.DuringDescription = null;
                    Equal(McpMod.TestDescription(card), "7,11", "nested render result");
                    Equal(card.DynamicVars.Values[0].PreviewValue, 7m, "nested scope restores parent intrinsic preview");
                };
                Equal(McpMod.TestBuild(true, () => McpMod.TestDescription(card)), "7,11", "outer render result");
                Restored(card);
            })
        };
        int failures = 0;
        foreach (var (name, run) in cases)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"{cases.Length - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}

// The harness compiles the actual CardValues implementation, using small game
// type stand-ins to inject failures at an otherwise hard-to-reach preview hook.
namespace MegaCrit.Sts2.Core.Entities.Cards
{
    public enum PileType { Hand }
    public enum CardPreviewMode { None }
}

namespace MegaCrit.Sts2.Core.Localization.DynamicVars
{
    public class DynamicVar
    {
        private readonly decimal _intrinsic;
        public decimal EnchantedValue { get; set; }
        public decimal PreviewValue { get; set; }
        public bool ThrowPreview { get; set; }
        public DynamicVar(decimal basis, decimal enchanted, decimal preview)
        { _intrinsic = enchanted; EnchantedValue = enchanted; PreviewValue = preview; }
        public void UpdateCardPreview(CardModel card, CardPreviewMode mode, object? target, bool runGlobalHooks)
        {
            if (runGlobalHooks) throw new InvalidOperationException("global hooks must be disabled");
            EnchantedValue = _intrinsic; PreviewValue = _intrinsic;
            if (ThrowPreview) throw new InvalidOperationException("injected dynamic variable failure");
        }
    }
    public class DynamicVarSet
    {
        public List<DynamicVar> Values { get; }
        public DynamicVarSet(params DynamicVar[] values) => Values = values.ToList();
        public void ClearPreview() { foreach (var variable in Values) variable.PreviewValue = 0; }
    }
}

namespace MegaCrit.Sts2.Core.Models
{
    public class ModelId { public string Entry => "TEST_CARD"; }
    public class CardModel
    {
        public ModelId Id { get; } = new ModelId();
        public DynamicVarSet DynamicVars { get; }
        public EnchantmentModel? Enchantment { get; set; }
        public bool ThrowDescription { get; set; }
        public Action? DuringDescription { get; set; }
        public int FallbackReads { get; private set; }
        public string Description { get { FallbackReads++; return "powered fallback"; } }
        public CardModel(params DynamicVar[] variables) => DynamicVars = new DynamicVarSet(variables);
        public string GetDescriptionForPile(PileType pile)
        {
            DuringDescription?.Invoke();
            if (ThrowDescription) throw new InvalidOperationException("injected card formatting failure");
            return string.Join(",", DynamicVars.Values.Select(variable => variable.PreviewValue));
        }
    }
    public class EnchantmentModel
    {
        public DynamicVarSet DynamicVars { get; }
        public EnchantmentModel(params DynamicVar[] variables) => DynamicVars = new DynamicVarSet(variables);
    }
}

namespace STS2_MCP
{
    public static partial class McpMod
    {
        public static T TestBuild<T>(bool unpowered, Func<T> build) => WithCardValues(unpowered, build);
        public static string? TestDescription(CardModel card) => SafeGetCardDescription(card);
        public static bool TestValueMode => UnpoweredValues;
        private static string? SafeGetText(Func<object?> get) => get()?.ToString();
        internal static string StripRichTextTags(string text) => text;
    }
}
