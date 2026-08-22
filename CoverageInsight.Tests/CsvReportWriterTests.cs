using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoverageInsight.Models;
using CoverageInsight.Reporting;
using Xunit;

namespace CoverageInsight.Tests;

public class CsvReportWriterTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
            try { File.Delete(path); } catch { /* best effort */ }
    }

    private static CoverageReport Sample()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report.xml" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Domain" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Calculator", SourceFile = @"C:\src\Calc.cs" };
        var method = new CoverageNode
        {
            Kind = NodeKind.Method,
            Name = "Total(decimal, string)",
            LinesCovered = 6,
            LinesPartiallyCovered = 1,
            LinesNotCovered = 3,
            SourceFile = @"C:\src\Calc.cs",
            FirstLine = 12
        };
        root.Add(module); module.Add(space); space.Add(type); type.Add(method);
        root.Aggregate();

        return new CoverageReport { SourcePath = "report.xml", FormatName = "test", Root = root };
    }

    private string[] Write(CoverageReport report)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-{Guid.NewGuid():N}.csv");
        _temp.Add(path);
        CsvReportWriter.Write(report, path);
        return File.ReadAllLines(path);
    }

    [Fact]
    public void There_is_one_row_per_node_below_the_root()
    {
        var lines = Write(Sample());
        Assert.Equal(5, lines.Length); // header + module + namespace + type + method
    }

    [Fact]
    public void The_header_names_every_column_that_is_written()
    {
        var lines = Write(Sample());
        var columns = lines[0].TrimStart('\uFEFF').Split(',').Length;

        foreach (var row in lines.Skip(1))
            Assert.Equal(columns, SplitCsv(row).Length);
    }

    [Fact]
    public void Coverable_lines_equal_covered_plus_partial_plus_missed()
    {
        var lines = Write(Sample());
        var header = lines[0].TrimStart('\uFEFF').Split(',');
        int Col(string name) => Array.IndexOf(header, name);

        foreach (var row in lines.Skip(1))
        {
            var f = SplitCsv(row);
            var sum = int.Parse(f[Col("LinesCovered")]) + int.Parse(f[Col("LinesPartial")])
                                                        + int.Parse(f[Col("LinesNotCovered")]);
            Assert.Equal(int.Parse(f[Col("CoverableLines")]), sum);
        }
    }

    /// <summary>A signature with parameters must not become extra columns.</summary>
    [Fact]
    public void Values_containing_commas_are_quoted()
    {
        var row = Write(Sample()).Single(l => l.Contains("Total("));

        Assert.Contains("\"", row);
        Assert.Equal(13, SplitCsv(row).Length);
    }

    [Fact]
    public void The_percentage_is_written_with_an_invariant_decimal_point()
    {
        var row = Write(Sample()).Single(l => l.Contains("Total("));
        Assert.Contains("60.00", row);
    }

    [Fact]
    public void A_byte_order_mark_is_written_so_excel_reads_utf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-{Guid.NewGuid():N}.csv");
        _temp.Add(path);
        CsvReportWriter.Write(Sample(), path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

/// <summary>
/// The rollup everything else rests on. If these are wrong every export is wrong in the
/// same direction, which is exactly the failure a reader cannot detect.
/// </summary>
public class CoverageNodeTests
{
    private static CoverageNode Method(string name, int covered, int partial, int missed)
        => new()
        {
            Kind = NodeKind.Method, Name = name,
            LinesCovered = covered, LinesPartiallyCovered = partial, LinesNotCovered = missed
        };

    [Fact]
    public void Aggregate_sums_the_children()
    {
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "T" };
        type.Add(Method("a", 3, 1, 2));
        type.Add(Method("b", 5, 0, 4));
        type.Aggregate();

        Assert.Equal(8, type.LinesCovered);
        Assert.Equal(1, type.LinesPartiallyCovered);
        Assert.Equal(6, type.LinesNotCovered);
        Assert.Equal(15, type.CoverableLines);
    }

    /// <summary>
    /// Cobertura reports type totals that can include members never listed as methods,
    /// so a self-measured node must keep its own numbers rather than be recomputed.
    /// </summary>
    [Fact]
    public void Aggregate_leaves_a_self_measured_node_alone()
    {
        var type = new CoverageNode
        {
            Kind = NodeKind.Class, Name = "T", SelfMeasured = true,
            LinesCovered = 100, LinesNotCovered = 1
        };
        type.Add(Method("a", 3, 0, 2));
        type.Aggregate();

        Assert.Equal(100, type.LinesCovered);
        Assert.Equal(1, type.LinesNotCovered);
    }

    [Fact]
    public void Partial_lines_count_against_the_percentage()
    {
        var node = Method("a", 5, 5, 0);
        Assert.Equal(50d, node.LinePercent);
    }

    [Fact]
    public void A_node_with_no_lines_reports_zero_rather_than_dividing_by_zero()
    {
        var node = Method("a", 0, 0, 0);

        Assert.False(node.HasData);
        Assert.Equal(0d, node.LinePercent);
        Assert.Equal("n/a", node.PercentText);
    }

    [Fact]
    public void Bar_fractions_sum_to_one_when_there_is_data()
    {
        var node = Method("a", 3, 2, 5);
        Assert.Equal(1d, node.BarCovered + node.BarPartial + node.BarUncovered, 6);
    }

    [Fact]
    public void An_empty_node_paints_a_full_neutral_bar()
    {
        var node = Method("a", 0, 0, 0);
        Assert.Equal(1d, node.BarUncovered);
        Assert.False(node.HasData);
    }

    [Fact]
    public void Full_path_walks_the_ancestors_but_omits_the_report()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report.xml" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Calc" };
        var method = Method("Run()", 1, 0, 0);
        root.Add(module); module.Add(space); space.Add(type); type.Add(method);

        Assert.Equal("App.dll › Acme › Calc › Run()", method.FullPath);
        Assert.DoesNotContain("report.xml", method.FullPath);
    }

    [Fact]
    public void Descendants_returns_the_whole_subtree_but_not_the_node_itself()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "r" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "m" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "n" };
        root.Add(module); module.Add(space);

        Assert.Equal(2, root.Descendants().Count());
        Assert.DoesNotContain(root, root.Descendants());
    }

    [Fact]
    public void Cloning_pins_the_totals_so_a_filtered_copy_cannot_drift()
    {
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "T" };
        type.Add(Method("a", 10, 0, 5));
        type.Aggregate();

        var copy = type.CloneShallow();
        copy.Aggregate();                     // no children, and pinned anyway

        Assert.True(copy.SelfMeasured);
        Assert.Equal(10, copy.LinesCovered);
        Assert.Equal(5, copy.LinesNotCovered);
    }

    [Fact]
    public void Missed_lines_survive_a_clone()
    {
        var node = Method("a", 0, 0, 3);
        node.AddMissedLines(10, 12);

        Assert.Equal("10-12", node.CloneShallow().MissedRangeText);
    }

    [Fact]
    public void Expanding_recursively_reaches_every_descendant()
    {
        var root = new CoverageNode { Kind = NodeKind.Module, Name = "m" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "n" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "t" };
        root.Add(space); space.Add(type);

        root.SetExpandedRecursive(true);
        Assert.All(root.Descendants(), n => Assert.True(n.IsExpanded));
    }
}
