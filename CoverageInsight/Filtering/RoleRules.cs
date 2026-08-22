using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoverageInsight.Filtering;

/// <summary>
/// Once out-of-scope code is excluded, raw line count is a weak sort key. The sharper
/// question is which code fails *silently*.
///
/// UI failures are visible by definition, and I/O tends to throw on malformed input, so
/// both carry runtime signal that a test would duplicate. Parsers, normalisers and
/// derived-value code fail differently: they return a plausible wrong answer. Nothing
/// throws, nothing is logged, and the wrong number reaches something someone acts on.
/// That class is what tests are uniquely able to catch, so it keeps full weight.
///
/// Weighting only ever demotes. Anything unmatched stays at 1.0, so a type this list
/// has never heard of is never quietly pushed down the queue.
///
/// These patterns are naming heuristics and will need tuning per codebase — that's why
/// they live here, in one visible place, rather than inside the ranking code.
/// </summary>
public sealed class RoleRule
{
    private Regex[]? _compiled;

    public required string Name { get; init; }
    public required double Weight { get; init; }
    public required IReadOnlyList<string> Patterns { get; init; }

    private Regex[] Compiled => _compiled ??= Patterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
        .ToArray();

    public bool Matches(string text) => Compiled.Any(r => r.IsMatch(text));
}

public static class RoleRules
{
    /// <summary>Failures show up in the output, so a reader notices without a test.</summary>
    public static readonly RoleRule Presentation = new()
    {
        Name = "presentation",
        Weight = 0.4,
        Patterns = new[]
        {
            @"\w*(Report|ReportWriter|Renderer|Printer|Layout|Theme|Menu|Badge|Chrome)\b",
            @"(^|\.)(Presentation|Rendering|Printing|Layout)(\.|$)"
        }
    };

    /// <summary>Partial signal: malformed input usually throws rather than lying.</summary>
    public static readonly RoleRule Io = new()
    {
        Name = "io",
        Weight = 0.7,
        Patterns = new[]
        {
            @"\w*(Repository|Store|Storage|FileReader|FileWriter|Loader|Saver|Stream|Db|Dao)\b",
            @"(^|\.)(Persistence|Storage|Repositories|Data)(\.|$)"
        }
    };

    /// <summary>Wrong answers with no runtime signal — the class tests exist to catch.</summary>
    public static readonly RoleRule Drift = new()
    {
        Name = "drift",
        Weight = 1.0,
        Patterns = new[]
        {
            @"\w*(Converter|Parser|Normalizer|Normaliser|Calculator|Mapper|Serializer|Formatter|Validator|Comparer|Resolver|Aggregator|Accumulator|Rule|Rules|Policy|Engine|Math)\b",
            @"(^|\.)(Rules|Domain|Calculation|Normalization)(\.|$)"
        }
    };

    /// <summary>Unmatched keeps full weight — an unknown type is never demoted.</summary>
    public const string Unclassified = "other";
    public const double UnclassifiedWeight = 1.0;

    private static readonly RoleRule[] Ordered = { Presentation, Io, Drift };

    public static (string Role, double Weight) Classify(string namespaceName, string typeName)
    {
        var text = string.IsNullOrEmpty(namespaceName) ? typeName : namespaceName + "." + typeName;

        foreach (var rule in Ordered)
            if (rule.Matches(text))
                return (rule.Name, rule.Weight);

        return (Unclassified, UnclassifiedWeight);
    }

    public static string Help =>
        "Rank = lines missed × role weight. Weighting only demotes code that fails visibly:\n"
        + $"  presentation ×{Presentation.Weight:0.0} — failures show in the output\n"
        + $"  io           ×{Io.Weight:0.0} — malformed input usually throws\n"
        + $"  drift        ×{Drift.Weight:0.0} — wrong answers, no runtime signal\n"
        + $"  other        ×{UnclassifiedWeight:0.0} — unmatched, never demoted\n\n"
        + "Naming heuristics; tune them in Filtering/RoleRules.cs for your codebase.";
}
