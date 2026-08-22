using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CoverageInsight.Models;

/// <summary>Collapses line numbers into ranges: 118,119,120,121,129 becomes "118-121;129".</summary>
public static class LineRanges
{
    public static string Collapse(IEnumerable<int> lines)
    {
        var ordered = lines.Distinct().OrderBy(n => n).ToList();
        if (ordered.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        int start = ordered[0], previous = ordered[0];

        void Flush()
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(start);
            if (previous != start) sb.Append('-').Append(previous);
        }

        foreach (var line in ordered.Skip(1))
        {
            if (line == previous + 1) { previous = line; continue; }
            Flush();
            start = previous = line;
        }
        Flush();

        return sb.ToString();
    }
}
