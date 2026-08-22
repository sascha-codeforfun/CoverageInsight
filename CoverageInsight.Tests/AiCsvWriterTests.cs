using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CoverageInsight.Models;
using CoverageInsight.Reporting;
using Xunit;

namespace CoverageInsight.Tests;

/// <summary>
/// These encode the acceptance checks a consumer of the export wrote against it. Checks
/// 5 and 6 are the ones that caught a real bug: an implementation that handles state
/// machines but misses local functions looks like it worked.
/// </summary>
public class AiCsvWriterTests
{
    private static CoverageReport Report()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report.xml" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        root.Add(module);

        var domain = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Domain" };
        module.Add(domain);

        var analyzer = new CoverageNode { Kind = NodeKind.Class, Name = "Analyzer" };
        domain.Add(analyzer);
        analyzer.Add(Method("BuildReport(Item)", 5, 0, 102, 400, 501));
        analyzer.Add(Method("RunAsync(String)", 10, 2, 31, 200, 230));

        // the same two methods' generated halves, on generated nested types
        var lambdas = new CoverageNode { Kind = NodeKind.Class, Name = "Analyzer.<>c" };
        domain.Add(lambdas);
        lambdas.Add(Method("<BuildReport>b__9_4(Item)", 0, 0, 67, 510, 576));

        var stateMachine = new CoverageNode { Kind = NodeKind.Class, Name = "Analyzer.<RunAsync>d__8" };
        domain.Add(stateMachine);
        stateMachine.Add(Method("MoveNext()", 0, 0, 100, 240, 339));

        var parser = new CoverageNode { Kind = NodeKind.Class, Name = "EntryParser" };
        domain.Add(parser);
        parser.Add(Method("Read(Reader)", 4, 0, 6, 20, 25));
        parser.Add(Method("<Read>g__Facts|12_1(String)", 0, 0, 30, 40, 69));
        parser.Add(Method("<Read>g__Names|12_2(String)", 0, 0, 40, 80, 119));
        parser.Add(Method("Convert(object, Type, object)", 1, 0, 9, 130, 138));

        // fully covered — must not appear at all
        var clean = new CoverageNode { Kind = NodeKind.Class, Name = "RateTable" };
        domain.Add(clean);
        clean.Add(Method("Lookup()", 40, 0, 0, 0, 0));

        root.Aggregate();
        return new CoverageReport { SourcePath = "report.xml", FormatName = "test", Root = root };
    }

    private static CoverageNode Method(string name, int covered, int partial, int missed, int from, int to)
    {
        var node = new CoverageNode
        {
            Kind = NodeKind.Method,
            Name = name,
            LinesCovered = covered,
            LinesPartiallyCovered = partial,
            LinesNotCovered = missed
        };
        if (missed > 0) node.AddMissedLines(from, to);
        return node;
    }

    private static string Build(bool fold = true)
    {
        var report = Report();
        return AiCsvWriter.Build(report, report.Root, new AiCsvOptions { FoldGenerated = fold });
    }

    private static string[] Rows(string csv)
        => csv.Split('\n')
              .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("ns,"))
              .Select(l => l.TrimEnd('\r'))
              .ToArray();

    private static string RowFor(string csv, string method)
        => Rows(csv).Single(r => r.Split(',')[2] == method);

    /// <summary>
    /// Regression from a real report: the parser normalises generated nested types away
    /// so the tree groups them under the declaring type, which destroys the only record
    /// of which method a state machine belongs to. Every async method then arrives as
    /// "MoveNext". The fold has to use the type name the report actually gave.
    /// </summary>
    [Fact]
    public void An_async_body_resolves_to_its_method_not_to_MoveNext()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "r" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
        // as the parser leaves it: type normalised, raw name kept on the method
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Worker" };
        var body = Method("MoveNext()", 0, 0, 40, 100, 139);
        body.DeclaringType = "Worker.<RunAsync>d__12";
        type.Add(body);
        root.Add(module); module.Add(space); space.Add(type);
        root.Aggregate();

        var report = new CoverageReport { SourcePath = "r", FormatName = "t", Root = root };
        var csv = AiCsvWriter.Build(report, root, new AiCsvOptions());

        Assert.Contains("RunAsync", csv);
        Assert.DoesNotContain("MoveNext", csv);
    }

    /// <summary>Check 5: a method and its lambda are one row, 102 + 67.</summary>
    [Fact]
    public void Folding_merges_a_method_with_its_lambda()
    {
        var fields = RowFor(Build(), "BuildReport").Split(',');
        Assert.Equal("169", fields[4]);
    }

    /// <summary>Check 6: the local functions fold in too, 6 + 30 + 40.</summary>
    [Fact]
    public void Folding_merges_a_method_with_its_local_functions()
    {
        var fields = RowFor(Build(), "Read").Split(',');
        Assert.Equal("76", fields[4]);
    }

    [Fact]
    public void Folding_merges_the_missed_ranges_rather_than_dropping_them()
    {
        var fields = RowFor(Build(), "Read").Split(',');
        Assert.Equal("20-25;40-69;80-119", fields[6]);
    }

    /// <summary>
    /// Regression from a real report: a lambda's lines lie inside its declaring method's
    /// span and the collector counts them in both members, so folding by addition
    /// overstated the biggest rows — the ones the ranking is built on.
    /// </summary>
    [Fact]
    public void Folding_counts_overlapping_lines_once()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "r" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Builder" };
        root.Add(module); module.Add(space); space.Add(type);

        // the method's own span covers 10-20; its lambda body sits at 14-16, inside it
        type.Add(Method("Build()", 0, 0, 11, 10, 20));
        type.Add(Method("<Build>b__1_0()", 0, 0, 3, 14, 16));
        root.Aggregate();

        var report = new CoverageReport { SourcePath = "r", FormatName = "t", Root = root };
        var csv = AiCsvWriter.Build(report, root, new AiCsvOptions());
        var fields = Rows(csv).Single(r => r.Contains("Build")).Split(',');

        Assert.Equal("11", fields[4]);      // not 14
        Assert.Equal("10-20", fields[6]);
    }

    /// <summary>Every row's count must agree with its own ranges.</summary>
    [Fact]
    public void Lines_missed_matches_the_distinct_lines_in_the_ranges()
    {
        foreach (var row in Rows(Build()))
        {
            var fields = row.Split(',');
            var missed = int.Parse(fields[4]);
            var distinct = fields[6].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Sum(part =>
                {
                    var bounds = part.Split('-');
                    return bounds.Length == 1 ? 1 : int.Parse(bounds[1]) - int.Parse(bounds[0]) + 1;
                });

            Assert.Equal(distinct, missed);
        }
    }

    [Fact]
    public void The_generated_column_says_what_was_folded()
    {
        Assert.Contains("local func Facts", RowFor(Build(), "Read"));
        Assert.Contains("lambda", RowFor(Build(), "BuildReport"));
        Assert.Contains("async", RowFor(Build(), "RunAsync"));
    }

    /// <summary>Check 4: nothing generated survives.</summary>
    [Fact]
    public void No_generated_syntax_reaches_the_output()
    {
        foreach (var row in Rows(Build()))
        {
            Assert.DoesNotContain("d__", row);
            Assert.DoesNotContain("b__", row);
            Assert.DoesNotContain("g__", row);
            Assert.DoesNotContain("<>c", row);
        }
    }

    /// <summary>Check 8: folding reduces the row count.</summary>
    [Fact]
    public void Folding_reduces_the_number_of_rows()
        => Assert.True(Rows(Build(fold: true)).Length < Rows(Build(fold: false)).Length);

    [Fact]
    public void Unfolded_output_keeps_every_generated_member_separate()
    {
        var rows = Rows(Build(fold: false));
        Assert.Contains(rows, r => r.Contains("MoveNext"));
        Assert.Contains(rows, r => r.Contains("b__9_4"));
    }

    [Fact]
    public void Fully_covered_members_are_omitted()
        => Assert.DoesNotContain(Rows(Build()), r => r.Contains("Lookup"));

    /// <summary>
    /// Ranking is what the file is for: the folded method outranks the one that only
    /// looked bigger before folding.
    /// </summary>
    [Fact]
    public void Rows_are_sorted_by_rank_descending()
    {
        var ranks = Rows(Build()).Select(r => double.Parse(r.Split(',')[8],
            System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(ranks.OrderByDescending(x => x), ranks);
        Assert.Equal("BuildReport", Rows(Build())[0].Split(',')[2]);
    }

    [Fact]
    public void The_header_states_the_filters_the_scope_and_the_partial_convention()
    {
        var csv = AiCsvWriter.Build(Report(), Report().Root,
            new AiCsvOptions { FilterNote = "excluded 2 types", ScopeNote = "App.dll" });

        Assert.Contains("# scope: App.dll", csv);
        Assert.Contains("# filters: excluded 2 types", csv);
        Assert.Contains("# partial lines counted as uncovered", csv);
        Assert.Contains("# totals: before ", csv);
    }

    /// <summary>
    /// Absence has to be readable as "fully covered" rather than "a filter ate it",
    /// so an unfiltered export must say so rather than leaving the line blank.
    /// </summary>
    [Fact]
    public void An_unfiltered_export_says_so_explicitly()
        => Assert.Contains("# filters: none", Build());

    /// <summary>A signature with parameters must not split into extra columns.</summary>
    [Fact]
    public void Method_names_containing_commas_are_quoted()
    {
        var row = Rows(Build(fold: false)).First(r => r.Contains("Convert("));

        Assert.Contains("\"Convert(object, Type, object)\"", row);
        Assert.Equal(10, SplitCsv(row).Length);
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
