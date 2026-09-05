using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

namespace STS2_MCP;

public static partial class McpMod
{
    private const int DefaultWikiSearchLimit = 10;
    private const int MaxWikiSearchLimit = 50;

    // A listing is not a search: the 50-result search cap exists to keep a fuzzy
    // query from dumping the catalog, and an explicit request for the whole list
    // has already asked for that.  Building a static card catalog is the case
    // this serves -- 543 fuzzy queries, one per card, was the alternative, and
    // whatever never ranked in a query's top 50 was silently absent from it.
    private const int MaxWikiListLimit = 5000;

    private static void HandleGetWiki(HttpListenerRequest request, HttpListenerResponse response)
    {
        var query = request.QueryString["query"] ?? request.QueryString["q"] ?? "";
        var itemType = request.QueryString["type"] ?? request.QueryString["item_type"] ?? "all";
        var scope = request.QueryString["scope"] ?? "discovered";
        var limit = ParseWikiLimit(request.QueryString["limit"], string.IsNullOrWhiteSpace(query));

        try
        {
            var dataTask = RunOnMainThread(() => BuildWikiSearch(query, itemType, limit, scope));
            SendJson(response, dataTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to search wiki: {ex.Message}");
        }
    }

    internal static object SearchWiki(string query = "", string itemType = "all", int? limit = null,
        string scope = "discovered")
    {
        var listing = string.IsNullOrWhiteSpace(query);
        var normalized = limit.HasValue
            ? NormalizeWikiLimit(limit.Value, listing)
            : (listing ? MaxWikiListLimit : DefaultWikiSearchLimit);
        return BuildWikiSearch(query, itemType, normalized, scope);
    }

    // "discovered" (the default) keeps the profile's own spoiler gate; "all"
    // searches ModelDb outright.  A tool building a static catalog wants "all":
    // a card the profile has not met still has a fully resolved description,
    // and without it that card reaches a deck with no text at all.
    private static string? NormalizeWikiScope(string scope)
    {
        return (scope ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "discovered" => "discovered",
            "all" => "all",
            _ => null
        };
    }

    private static Dictionary<string, object?> BuildWikiSearch(string query, string itemType, int limit,
        string scope = "discovered")
    {
        var progress = SaveManager.Instance?.Progress;
        var saveManager = SaveManager.Instance;
        if (progress == null || saveManager == null)
            return Error("No profile data available.");

        var normalizedItemType = NormalizeWikiItemType(itemType);
        if (normalizedItemType == null)
            return Error("item_type must be one of: all, card, relic.");

        var normalizedScope = NormalizeWikiScope(scope);
        if (normalizedScope == null)
            return Error("scope must be one of: discovered, all.");
        var everything = normalizedScope == "all";

        var discoveredCards = progress.DiscoveredCards
            .Select(id => id.Entry)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveredRelics = progress.DiscoveredRelics
            .Select(id => id.Entry)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<WikiCandidate>();
        if (normalizedItemType is "all" or "card")
            candidates.AddRange(BuildCardWikiCandidates(everything ? null : discoveredCards));
        if (normalizedItemType is "all" or "relic")
            candidates.AddRange(BuildRelicWikiCandidates(everything ? null : discoveredRelics));

        // No query is a LISTING: every candidate in name order, unscored.  A
        // fuzzy score would only impose an arbitrary order on an enumeration
        // nobody searched, and dropping the `Score > 0` filter with it is the
        // point -- that filter is what a listing must not have.
        var listing = string.IsNullOrWhiteSpace(query);

        // Counted before `Take`, so `truncated` says whether the limit actually
        // cut anything.  It has to be the ELIGIBLE count, not `candidates`: a
        // search's non-matching candidates are not results withheld.
        var eligible = listing
            ? candidates.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => (Candidate: candidate, Score: 0.0))
                .ToList()
            : candidates
                .Select(candidate => (Candidate: candidate, Score: ScoreWikiCandidate(query, candidate)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var matches = eligible
            .Take(limit)
            .Select(match => BuildWikiResult(match.Candidate, match.Score))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["profile_id"] = saveManager.CurrentProfileId,
            ["query"] = query,
            ["mode"] = listing ? "list" : "search",
            ["item_type"] = normalizedItemType,
            ["limit"] = limit,
            ["truncated"] = eligible.Count > matches.Count,
            ["scope"] = everything ? "all_cards_and_relics" : "active_profile_discovered_cards_and_relics",
            ["selection_policy"] = (everything, listing) switch
            {
                (true, true) => "Lists every card and relic the game defines, in name order, ignoring the active profile's discovery progress.",
                (true, false) => "Searches every card and relic the game defines, ignoring the active profile's discovery progress, then returns the best fuzzy matches.",
                (false, true) => "Lists every card and relic discovered by the active profile, in name order.",
                (false, false) => "Searches only cards and relics discovered by the active profile, then returns the best fuzzy matches instead of exposing the full catalog."
            },
            ["counts"] = new Dictionary<string, object?>
            {
                ["discovered_cards"] = discoveredCards.Count,
                ["discovered_relics"] = discoveredRelics.Count,
                ["searched"] = candidates.Count,
                ["returned"] = matches.Count
            },
            ["results"] = matches
        };
    }

    private static int ParseWikiLimit(string? rawLimit, bool listing = false)
    {
        if (!int.TryParse(rawLimit, out var limit))
            return listing ? MaxWikiListLimit : DefaultWikiSearchLimit;
        return NormalizeWikiLimit(limit, listing);
    }

    private static int NormalizeWikiLimit(int limit, bool listing = false)
        => Math.Clamp(limit, 1, listing ? MaxWikiListLimit : MaxWikiSearchLimit);

    private static string? NormalizeWikiItemType(string? itemType)
    {
        var value = (itemType ?? "all").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "all" or "any" => "all",
            "card" or "cards" => "card",
            "relic" or "relics" => "relic",
            _ => null
        };
    }

    // Enumerate the game's canonical model registry rather than reflectively
    // constructing fresh instances per-type. Fresh instances never have
    // InitId() called on them (that only happens for instances stored in
    // ModelDb._contentById), so card.Id.Entry comes back empty and the
    // discoveredIds filter drops everything. ModelDb.AllCards also naturally
    // includes mod-injected cards.
    // A null `discoveredIds` means scope=all: every card ModelDb defines,
    // whether or not the profile has met it.
    private static IEnumerable<WikiCandidate> BuildCardWikiCandidates(HashSet<string>? discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in ModelDb.AllCards)
        {
            var id = SafeGetText(() => card.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || (discoveredIds != null && !discoveredIds.Contains(id)))
                continue;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "card",
                Id: id,
                Name: SafeGetText(() => card.Title) ?? id,
                SearchText: BuildWikiSearchText("card", id, SafeGetText(() => card.Title), SafeGetText(() => card.Type), SafeGetText(() => card.Rarity)),
                Card: card,
                Relic: null));
        }

        return byId.Values;
    }

    // Null `discoveredIds` means scope=all, as for cards above.
    private static IEnumerable<WikiCandidate> BuildRelicWikiCandidates(HashSet<string>? discoveredIds)
    {
        var byId = new Dictionary<string, WikiCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var relic in ModelDb.AllRelics)
        {
            var id = SafeGetText(() => relic.Id.Entry);
            if (string.IsNullOrWhiteSpace(id) || (discoveredIds != null && !discoveredIds.Contains(id)))
                continue;

            byId.TryAdd(id, new WikiCandidate(
                Kind: "relic",
                Id: id,
                Name: SafeGetText(() => relic.Title) ?? id,
                SearchText: BuildWikiSearchText("relic", id, SafeGetText(() => relic.Title), SafeGetText(() => relic.Rarity)),
                Card: null,
                Relic: relic));
        }

        return byId.Values;
    }

    private static Dictionary<string, object?> BuildWikiResult(WikiCandidate candidate, double score)
    {
        if (candidate.Card != null)
            return BuildWikiCardResult(candidate, score);
        if (candidate.Relic != null)
            return BuildWikiRelicResult(candidate, score);

        return new Dictionary<string, object?>
        {
            ["item_type"] = candidate.Kind,
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3)
        };
    }

    private static Dictionary<string, object?> BuildWikiCardResult(WikiCandidate candidate, double score)
    {
        var card = candidate.Card!;
        var upgraded = SafeBuildUpgradedCardPreview(card);
        var upgradedVariant = upgraded != null
            ? BuildWikiCardVariant(upgraded, "upgraded")
            : card.IsUpgradable
                ? new Dictionary<string, object?>
                {
                    ["variant"] = "upgraded",
                    ["description"] = SafeGetCardUpgradePreviewDescription(card),
                    ["preview_available"] = false
                }
                : null;

        var result = new Dictionary<string, object?>
        {
            ["item_type"] = "card",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3),
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["type"] = SafeGetText(() => card.Type),
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["base"] = BuildWikiCardVariant(card, "base"),
            ["upgraded"] = upgradedVariant
        };

        return result;
    }

    private static Dictionary<string, object?> BuildWikiCardVariant(CardModel card, string variant)
    {
        return new Dictionary<string, object?>
        {
            ["variant"] = variant,
            ["id"] = SafeGetText(() => card.Id.Entry),
            ["name"] = SafeGetText(() => card.Title),
            ["type"] = SafeGetText(() => card.Type),
            ["rarity"] = SafeGetText(() => card.Rarity),
            ["cost"] = GetCostDisplay(card),
            ["star_cost"] = GetStarCostDisplay(card),
            ["description"] = SafeGetCardDescription(card),
            ["is_upgraded"] = card.IsUpgraded,
            ["is_upgradable"] = card.IsUpgradable,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["keywords"] = BuildHoverTips(card.HoverTips)
        };
    }

    private static Dictionary<string, object?> BuildWikiRelicResult(WikiCandidate candidate, double score)
    {
        var relic = candidate.Relic!;
        return new Dictionary<string, object?>
        {
            ["item_type"] = "relic",
            ["id"] = candidate.Id,
            ["name"] = candidate.Name,
            ["score"] = Math.Round(score, 3),
            ["rarity"] = SafeGetText(() => relic.Rarity),
            ["description"] = SafeGetText(() => relic.DynamicDescription),
            ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
        };
    }

    private static string BuildWikiSearchText(params string?[] values)
        => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static double ScoreWikiCandidate(string query, WikiCandidate candidate)
    {
        var queryNormalized = NormalizeSearchText(query);
        var candidateNormalized = NormalizeSearchText(candidate.SearchText);
        if (string.IsNullOrWhiteSpace(queryNormalized) || string.IsNullOrWhiteSpace(candidateNormalized))
            return 0;

        var queryCompact = CompactSearchText(queryNormalized);
        var candidateCompact = CompactSearchText(candidateNormalized);
        var nameCompact = CompactSearchText(candidate.Name);
        var idCompact = CompactSearchText(candidate.Id);

        if (queryCompact.Equals(nameCompact, StringComparison.OrdinalIgnoreCase)
            || queryCompact.Equals(idCompact, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (nameCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase)
            || idCompact.Contains(queryCompact, StringComparison.OrdinalIgnoreCase))
            return 850;

        var queryTokens = TokenizeSearchText(queryNormalized);
        var candidateTokens = TokenizeSearchText(candidateNormalized);
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        double total = 0;
        var strongMatches = 0;
        foreach (var queryToken in queryTokens)
        {
            var best = candidateTokens.Max(candidateToken => TokenSimilarity(queryToken, candidateToken));
            total += best;
            if (best >= 0.72)
                strongMatches++;
        }

        var average = total / queryTokens.Count;
        var score = average * 700;
        if (strongMatches > 0)
            score += strongMatches * 35;

        return score >= 300 ? score : 0;
    }

    private static string NormalizeSearchText(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string CompactSearchText(string value)
        => NormalizeSearchText(value).Replace(" ", "", StringComparison.Ordinal);

    private static List<string> TokenizeSearchText(string value)
        => NormalizeSearchText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double TokenSimilarity(string queryToken, string candidateToken)
    {
        if (queryToken.Equals(candidateToken, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        // Require >=3 chars on the candidate token for the substring shortcut:
        // single-letter words like "a"/"I" otherwise trivially substring-match
        // arbitrary queries and produce spurious results for gibberish input.
        if (candidateToken.Length >= 3
            && (candidateToken.Contains(queryToken, StringComparison.OrdinalIgnoreCase)
                || queryToken.Contains(candidateToken, StringComparison.OrdinalIgnoreCase)))
            return 0.88;

        var distance = LevenshteinDistance(queryToken, candidateToken);
        var maxLength = Math.Max(queryToken.Length, candidateToken.Length);
        return maxLength == 0 ? 0 : 1.0 - ((double)distance / maxLength);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record WikiCandidate(
        string Kind,
        string Id,
        string Name,
        string SearchText,
        CardModel? Card,
        RelicModel? Relic);
}
