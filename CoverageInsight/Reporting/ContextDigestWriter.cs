using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CoverageInsight.Filtering;
using CoverageInsight.Models;

namespace CoverageInsight.Reporting;

/// <summary>
/// Writes the smallest thing that still answers "what should I test next".
/// Everything here is chosen to cut tokens: no absolute source paths, no block or
/// branch columns, no rows at or above target, and namespaces stated once as a
/// dotted prefix rather than repeated per row.
/// </summary>
public static class ContextDigestWriter
{
    private const int MaxMethodsPerType = 6;

    private sealed class MethodRow
    {
        public string Name = string.Empty;
        public int Missed;
        public int Coverable;
        public double Percent => Coverable == 0 ? 0 : 100d * (Coverable - Missed) / Coverable;
    }

    public static string Build(CoverageReport report, double threshold, string? filterNote)
    {
        var root = report.Root;
        var classes = root.Descendants().Where(c => c.Kind == NodeKind.Class && c.HasData).ToList();

        // Ranked the same way as the CSV: by what's missing, weighted by how silently
        // the code fails. Ordering by percentage alone puts a four-line converter above
        // a forty-line gap, which is the opposite of a work queue.
        var below = classes.Where(c => c.LinePercent < threshold)
                           .Select(c => (Type: c, Rank: c.LinesNotCovered * RoleRules.Classify(NamespaceOf(c), c.Name).Weight))
                           .OrderByDescending(x => x.Rank)
                           .ThenByDescending(x => x.Type.LinesNotCovered)
                           .ToList();
        var atTarget = classes.Count - below.Count;

        var sb = new StringBuilder();

        sb.Append("# Coverage digest — ").AppendLine(Path.GetFileName(report.SourcePath));
        sb.Append(report.FormatName).Append(" · ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.Append("Target ").Append(Num(threshold)).AppendLine("% line coverage. " +
            "Rows read: percent covered, -N lines never executed, path. Worst first.");
        sb.AppendLine();

        sb.Append("Totals: ").Append(Pct(root.LinePercent))
          .Append(" — ").Append(Num(root.LinesCovered)).Append('/')
          .Append(Num(root.CoverableLines)).Append(" lines");
        if (root.LinesPartiallyCovered > 0)
            sb.Append(", ").Append(Num(root.LinesPartiallyCovered)).Append(" partial");
        sb.Append(", ").Append(Num(root.LinesNotCovered)).AppendLine(" never hit");

        sb.Append("Scope: ")
          .Append(Count(root.Children.Count, "assembly", "assemblies")).Append(", ")
          .Append(Count(classes.Count, "type", "types")).Append(", ")
          .Append(Count(root.Descendants().Count(n => n.Kind == NodeKind.Method), "method", "methods"))
          .AppendLine();

        if (!string.IsNullOrWhiteSpace(filterNote))
            sb.Append("Filters: ").AppendLine(filterNote);

        // The export adds a note restating the filters; printing both wastes tokens
        // on the same fact.
        foreach (var note in report.Notes.Where(n => !RestatesFilters(n, filterNote)))
            sb.Append("Note: ").AppendLine(note);

        sb.AppendLine();

        if (below.Count == 0)
        {
            sb.Append("No type is below target. ").Append(classes.Count)
              .AppendLine(" types measured, all at or above it.");
            return sb.ToString();
        }

        sb.Append("## Below target (").Append(below.Count).AppendLine(", worst first)");
        sb.AppendLine();

        // Group by namespace so the prefix is written once instead of on every row,
        // keeping the worst-first order across the groups.
        foreach (var group in below.GroupBy(x => NamespaceOf(x.Type)))
        {
            sb.Append("### ").AppendLine(group.Key);

            foreach (var (type, _) in group)
            {
                sb.Append(Pct(type.LinePercent).PadRight(7))
                  .Append(Missed(type.LinesNotCovered).PadRight(6))
                  .AppendLine(type.Name);

                var methods = FoldMethods(type, threshold);

                // A single method that restates the type's numbers is pure duplication.
                if (methods.Count == 1 && methods[0].Missed == type.LinesNotCovered
                                       && methods[0].Coverable == type.CoverableLines)
                    continue;

                foreach (var method in methods.Take(MaxMethodsPerType))
                    sb.Append("  ").Append(Pct(method.Percent).PadRight(7))
                      .Append(Missed(method.Missed).PadRight(6))
                      .AppendLine(method.Name);

                if (methods.Count > MaxMethodsPerType)
                    sb.Append("  …").Append(methods.Count - MaxMethodsPerType)
                      .AppendLine(" more methods below target");
            }

            sb.AppendLine();
        }

        if (atTarget > 0)
            sb.Append("Omitted: ").Append(atTarget).AppendLine(" types at or above target.");

        return sb.ToString();
    }

    /// <summary>
    /// Folds generated members into their declaring method. Without this a method's
    /// lambdas arrive as separate one-line rows and crowd the real methods out of the
    /// per-type budget — the noise displaces exactly what the digest exists to show.
    /// </summary>
    private static List<MethodRow> FoldMethods(CoverageNode type, double threshold)
    {
        var folded = new Dictionary<string, MethodRow>(StringComparer.Ordinal);

        foreach (var method in type.Children.Where(m => m.HasData))
        {
            var name = MemberNames.Fold(method.DeclaringType ?? type.Name, method.Name).Method;

            if (!folded.TryGetValue(name, out var row))
            {
                row = new MethodRow { Name = name };
                folded[name] = row;
            }

            row.Missed += method.LinesNotCovered;
            row.Coverable += method.CoverableLines;
        }

        return folded.Values
            .Where(m => m.Percent < threshold)
            .OrderByDescending(m => m.Missed)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Rough token count — chars/4 is close enough for deciding what fits.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    private static bool RestatesFilters(string note, string? filterNote)
        => !string.IsNullOrWhiteSpace(filterNote)
           && note.Contains(filterNote!, StringComparison.Ordinal);

    private static string NamespaceOf(CoverageNode type)
        => type.Parent is { Kind: NodeKind.Namespace } ns ? ns.Name : "(global namespace)";

    private static string Count(int value, string singular, string plural)
        => value + " " + (value == 1 ? singular : plural);

    // Invariant throughout: a digest read on one machine and written on another
    // shouldn't change what "1.342" means.
    private static string Num(int value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Pct(double value)
        => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Missed(int lines)
        => "-" + lines.ToString(CultureInfo.InvariantCulture);
}
