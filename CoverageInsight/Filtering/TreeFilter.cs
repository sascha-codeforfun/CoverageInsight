using System;
using System.Collections.Generic;
using CoverageInsight.Models;

namespace CoverageInsight.Filtering;

/// <summary>What the display filters are currently set to. No UI types, so it can be exercised directly.</summary>
public sealed record FilterSettings
{
    public string Search { get; init; } = string.Empty;
    public bool OnlyBelowThreshold { get; init; }
    public bool HideMethods { get; init; }
    public double Threshold { get; init; } = 80;

    public bool Searching => !string.IsNullOrWhiteSpace(Search);
}

/// <summary>
/// The two passes that decide what a report shows and exports.
///
/// They're separated because they mean different things: scoping removes code from the
/// totals, so the percentage describes what's left, while filtering only hides rows and
/// must never move a number. Getting that backwards is silent, which is why this lives
/// apart from the view model where it can be tested.
/// </summary>
public static class TreeFilter
{
    /// <summary>
    /// Pass 1. Removes everything an active rule matches and lets the parents re-total.
    /// A parent that loses a child stops being self-measured, otherwise the headline would
    /// still be counting code that is no longer on screen.
    /// </summary>
    public static CoverageNode? Scope(CoverageNode node, IReadOnlyList<ExclusionRule> rules,
                                      Action<CoverageNode, ExclusionRule>? onExcluded = null)
    {
        var rule = ExclusionRules.FirstMatch(rules, node);
        if (rule is not null)
        {
            onExcluded?.Invoke(node, rule);
            return null;
        }

        var copy = node.CloneShallow(pinTotals: false);
        var lostChild = false;

        foreach (var child in node.Children)
        {
            var kept = Scope(child, rules, onExcluded);
            if (kept is null) lostChild = true;
            else copy.Add(kept);
        }

        if (lostChild)
            copy.SelfMeasured = false;

        // A container whose entire contents were excluded goes with them.
        if (node.Kind != NodeKind.Report && node.Children.Count > 0 && copy.Children.Count == 0)
            return null;

        return copy;
    }

    /// <summary>
    /// Pass 2. Hides rows without touching the numbers: every copy keeps the totals it
    /// arrived with. A node survives if it matches the filters itself or if anything
    /// beneath it did.
    /// </summary>
    public static CoverageNode? Filter(CoverageNode node, FilterSettings settings)
    {
        if (settings.HideMethods && node.Kind == NodeKind.Method)
            return null;

        var copy = node.CloneShallow();

        foreach (var child in node.Children)
        {
            var keptChild = Filter(child, settings);
            if (keptChild is not null)
                copy.Add(keptChild);
        }

        var nameMatches = !settings.Searching ||
                          node.Name.Contains(settings.Search, StringComparison.OrdinalIgnoreCase) ||
                          (node.SourceFile?.Contains(settings.Search, StringComparison.OrdinalIgnoreCase) ?? false);

        var passesCoverage = !settings.OnlyBelowThreshold ||
                             node.Kind is not (NodeKind.Class or NodeKind.Method) ||
                             (node.HasData && node.LinePercent < settings.Threshold);

        var keepSelf = nameMatches && passesCoverage;

        // An assembly or namespace only exists to hold things; if nothing under it
        // survived the filter, it shouldn't sit there as an empty row.
        var isContainer = node.Kind is NodeKind.Module or NodeKind.Namespace;
        if (copy.Children.Count == 0 && (isContainer || !keepSelf))
            return null;

        // While searching, open the path down to the hits.
        copy.IsExpanded = settings.Searching && copy.Children.Count > 0;
        return copy;
    }
}
