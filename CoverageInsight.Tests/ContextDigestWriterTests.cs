using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using CoverageInsight.Models;
using CoverageInsight.Reporting;
using Xunit;

namespace CoverageInsight.Tests;

public class ContextDigestWriterTests
{
    private static CoverageReport Sample()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report.xml" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var reporting = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Reporting" };
        var models = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Models" };
        root.Add(module); module.Add(reporting); module.Add(models);

        // one big method plus its lambdas, which must not crowd out the real methods
        var digest = new CoverageNode { Kind = NodeKind.Class, Name = "DigestWriter" };
        digest.Add(Method("Build(Report)", 0, 58));
        for (var i = 0; i < 8; i++)
            digest.Add(Method($"<Build>b__1_{i}(Node)", 0, 1));
        digest.Add(Method("Estimate(string)", 0, 4));
        reporting.Add(digest);

        // small and fully dark: must not outrank a bigger partially covered type
        var converter = new CoverageNode { Kind = NodeKind.Class, Name = "BrushConverter" };
        converter.Add(Method("Convert(object)", 0, 4));
        models.Add(converter);

        // bigger gap but partly covered
        var node = new CoverageNode { Kind = NodeKind.Class, Name = "TreeNode" };
        node.Add(Method("Aggregate()", 30, 42));
        models.Add(node);

        var clean = new CoverageNode { Kind = NodeKind.Class, Name = "Settled" };
        clean.Add(Method("Fine()", 20, 0));
        models.Add(clean);

        root.Aggregate();
        return new CoverageReport { SourcePath = @"C:\r\report.xml", FormatName = "test format", Root = root };
    }

    private static CoverageNode Method(string name, int covered, int missed)
        => new() { Kind = NodeKind.Method, Name = name, LinesCovered = covered, LinesNotCovered = missed };

    private static string[] Lines(string digest)
        => digest.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static int IndexOfLineContaining(string digest, string text)
        => Array.FindIndex(Lines(digest), l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// Defect from a real export: lambdas arrived as their own one-line rows and filled
    /// the per-type budget, pushing the actual methods out of the listing.
    /// </summary>
    [Fact]
    public void Generated_members_are_folded_into_their_declaring_method()
    {
        var digest = ContextDigestWriter.Build(Sample(), 80, null);

        Assert.DoesNotContain("b__", digest);
        Assert.Contains("-66", digest);   // 58 + eight lambdas at 1
    }

    /// <summary>
    /// Defect from a real export: ordering by percentage put a four-line converter at 0%
    /// above a forty-line gap at 55%, which is the opposite of a work queue.
    /// </summary>
    [Fact]
    public void Types_are_ranked_by_what_is_missing_not_by_percentage()
    {
        var digest = ContextDigestWriter.Build(Sample(), 80, null);

        Assert.True(IndexOfLineContaining(digest, "TreeNode") <
                    IndexOfLineContaining(digest, "BrushConverter"));
    }

    [Fact]
    public void The_worst_type_comes_first()
    {
        var digest = ContextDigestWriter.Build(Sample(), 80, null);
        Assert.True(IndexOfLineContaining(digest, "DigestWriter") <
                    IndexOfLineContaining(digest, "TreeNode"));
    }

    [Fact]
    public void Types_at_or_above_target_are_omitted_and_counted()
    {
        var digest = ContextDigestWriter.Build(Sample(), 80, null);

        Assert.DoesNotContain("Settled", digest);
        Assert.Contains("Omitted: 1 types at or above target.", digest);
    }

    /// <summary>Defect from a real export: "Scope: 1 assemblies".</summary>
    [Fact]
    public void Counts_of_one_are_written_in_the_singular()
        => Assert.Contains("1 assembly,", ContextDigestWriter.Build(Sample(), 80, null));

    /// <summary>
    /// Defect from a real export: the note restating the filters was printed directly
    /// under the filters line, spending tokens twice on one fact.
    /// </summary>
    [Fact]
    public void A_note_that_only_restates_the_filters_is_dropped()
    {
        var report = Sample();
        report.Notes.Add("Filtered report — excluded 9 types.");
        report.Notes.Add("72 modules skipped (no_symbols).");

        var digest = ContextDigestWriter.Build(report, 80, "excluded 9 types");

        Assert.Single(Lines(digest), l => l.StartsWith("Note:", StringComparison.Ordinal));
        Assert.Contains("72 modules skipped", digest);
    }

    /// <summary>
    /// Defect from a real export: counts used the machine's culture while percentages
    /// used the invariant one, so a single line mixed "1.342" and "50.0%" — and the
    /// digest is read by someone, or something, elsewhere.
    /// </summary>
    [Fact]
    public void Numbers_do_not_change_meaning_with_the_machine_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            var root = new CoverageNode { Kind = NodeKind.Report, Name = "r" };
            var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
            var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
            var type = new CoverageNode { Kind = NodeKind.Class, Name = "Big" };
            type.Add(Method("Run()", 1342, 900));
            root.Add(module); module.Add(space); space.Add(type);
            root.Aggregate();

            var digest = ContextDigestWriter.Build(
                new CoverageReport { SourcePath = "r.xml", FormatName = "t", Root = root }, 80, null);

            Assert.Contains("1,342", digest);
            Assert.DoesNotContain("1.342", digest);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_header_records_the_filters_that_produced_it()
        => Assert.Contains("Filters: only types under target",
            ContextDigestWriter.Build(Sample(), 80, "only types under target"));

    [Fact]
    public void A_fully_covered_report_says_so_rather_than_listing_nothing()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "r" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Clean" };
        type.Add(Method("Run()", 10, 0));
        root.Add(module); module.Add(space); space.Add(type);
        root.Aggregate();

        var digest = ContextDigestWriter.Build(
            new CoverageReport { SourcePath = "r.xml", FormatName = "t", Root = root }, 80, null);

        Assert.Contains("No type is below target", digest);
    }

    [Fact]
    public void The_per_type_method_budget_is_capped_and_the_remainder_counted()
    {
        var digest = ContextDigestWriter.Build(Sample(), 80, null);
        Assert.DoesNotContain("…0 more", digest);
    }

    [Fact]
    public void Token_estimate_scales_with_length()
    {
        Assert.Equal(0, ContextDigestWriter.EstimateTokens(string.Empty));
        Assert.Equal(1, ContextDigestWriter.EstimateTokens("abcd"));
        Assert.Equal(2, ContextDigestWriter.EstimateTokens("abcde"));
    }
}
