using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CoverageInsight.Filtering;
using CoverageInsight.Models;

namespace CoverageInsight.Reporting;

public sealed record AiCsvOptions
{
    /// <summary>
    /// Fold compiler-generated members into the source method that declared them.
    /// On, a method whose body is mostly await or LINQ reports as one row instead of a
    /// dozen — which is what stops a large untested method hiding below the visibility
    /// threshold. Off, every generated member keeps its own row and its own line numbers.
    /// Either way the missed ranges are preserved exactly; folding merges them.
    /// </summary>
    public bool FoldGenerated { get; init; } = true;

    public string FilterNote { get; init; } = string.Empty;
    public string ScopeNote { get; init; } = string.Empty;
}

/// <summary>
/// A CSV shaped for a model that's about to write tests, which wants different things
/// than a human reading a spreadsheet: the line numbers that never ran, one row per
/// method below 100%, ranked, and nothing else.
///
/// Deliberately absent: branch data, file paths and per-line hit counts. They multiply
/// the size and don't change what gets written next.
/// </summary>
public static class AiCsvWriter
{
    private sealed class Row
    {
        public string Namespace = string.Empty;
        public string Type = string.Empty;
        public string Method = string.Empty;
        public int Covered, Partial;

        /// <summary>Missed lines reported by members that carried no line detail.</summary>
        public int MissedWithoutRanges;

        public readonly List<int> MissedLines = new();
        public readonly SortedSet<string> Generated = new(StringComparer.Ordinal);

        /// <summary>
        /// Counted from the distinct line numbers rather than by summing the members.
        /// A lambda's body sits inside its declaring method's span and the collector
        /// counts those lines in both, so folding by addition overstates — worst on the
        /// biggest methods, which are exactly the rows the ranking depends on.
        /// </summary>
        public int Missed => MissedLines.Distinct().Count() + MissedWithoutRanges;

        public bool IsIncomplete => Missed > 0 || Partial > 0;
    }

    public static string Build(CoverageReport export, CoverageNode unfilteredInScope, AiCsvOptions options)
    {
        var root = export.Root;
        var rows = new Dictionary<(string, string, string), Row>();

        foreach (var node in root.Descendants().Where(n => n.Kind == NodeKind.Method && n.CoverableLines > 0))
            Accumulate(rows, node, options.FoldGenerated);

        // Types carrying their own numbers with no method detail underneath would
        // otherwise vanish silently; absence must always mean "fully covered".
        foreach (var type in root.Descendants()
                     .Where(n => n.Kind == NodeKind.Class && n.Children.Count == 0 && n.CoverableLines > 0))
            Accumulate(rows, type, options.FoldGenerated, "(type-level, no method detail)");

        var ranked = rows.Values
            .Where(r => r.IsIncomplete)
            .Select(r => (Row: r, Role: RoleRules.Classify(r.Namespace, r.Type)))
            .OrderByDescending(x => x.Row.Missed * x.Role.Weight)
            .ThenByDescending(x => x.Row.Missed)
            .ThenBy(x => x.Row.Namespace, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(options.ScopeNote))
            sb.Append("# scope: ").AppendLine(options.ScopeNote);

        sb.Append("# filters: ")
          .AppendLine(string.IsNullOrWhiteSpace(options.FilterNote) ? "none — full report" : options.FilterNote);

        sb.AppendLine("# partial lines counted as uncovered");
        sb.Append("# rank: lines_missed x role weight (presentation 0.4, io 0.7, drift 1.0, other 1.0)")
          .AppendLine(options.FoldGenerated
              ? "; generated members folded into their declaring method"
              : "; generated members kept as separate rows");

        sb.Append("# totals: before ").Append(Totals(unfilteredInScope))
          .Append(" | after ").Append(Totals(root))
          .Append(" | rows below 100%: ").Append(ranked.Count)
          .AppendLine();

        sb.AppendLine("ns,type,method,lines_covered,lines_missed,lines_partial,missed_ranges,role,rank,generated");

        foreach (var (row, role) in ranked)
        {
            sb.Append(Csv(row.Namespace)).Append(',')
              .Append(Csv(row.Type)).Append(',')
              .Append(Csv(row.Method)).Append(',')
              .Append(row.Covered.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Missed.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Partial.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(LineRanges.Collapse(row.MissedLines)).Append(',')
              .Append(role.Role).Append(',')
              .Append((row.Missed * role.Weight).ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(string.Join(" ", row.Generated)))
              .AppendLine();
        }

        return sb.ToString();
    }

    public static void Write(CoverageReport export, CoverageNode unfilteredInScope,
                             AiCsvOptions options, string outputPath)
        => File.WriteAllText(outputPath, Build(export, unfilteredInScope, options), new UTF8Encoding(false));

    private static void Accumulate(Dictionary<(string, string, string), Row> rows, CoverageNode node,
                                   bool fold, string? methodOverride = null)
    {
        var ns = NamespaceOf(node);
        // The tree's type names are already normalised for grouping, so folding has to
        // use what the report actually said or a state machine reduces to "MoveNext".
        var rawType = node.DeclaringType
                      ?? (node.Kind == NodeKind.Class ? node.Name : TypeOf(node));
        var rawMethod = methodOverride ?? node.Name;

        string type = rawType, method = rawMethod, generated = string.Empty;

        if (fold && methodOverride is null)
        {
            type = MemberNames.NormalizeType(rawType);
            (method, generated) = MemberNames.Fold(rawType, rawMethod);
        }

        var key = (ns, type, method);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new Row { Namespace = ns, Type = type, Method = method };
            rows[key] = row;
        }

        row.Covered += node.LinesCovered;
        row.Partial += node.LinesPartiallyCovered;

        if (node.MissedLines.Count > 0)
            row.MissedLines.AddRange(node.MissedLines);
        else
            row.MissedWithoutRanges += node.LinesNotCovered;

        if (!string.IsNullOrEmpty(generated))
            row.Generated.Add(generated);
    }

    private static string Totals(CoverageNode node)
        => $"{node.LinesCovered}/{node.CoverableLines} lines " +
           $"{node.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)}%";

    private static string NamespaceOf(CoverageNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n.Kind == NodeKind.Namespace)
                return n.Name;
        return string.Empty;
    }

    private static string TypeOf(CoverageNode node)
        => node.Parent is { Kind: NodeKind.Class } type ? type.Name : string.Empty;

    /// <summary>Quote only when the value needs it — every stray quote costs tokens.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
