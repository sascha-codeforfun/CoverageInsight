using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CoverageInsight.Models;
using CoverageInsight.Reporting;
using Xunit;

namespace CoverageInsight.Tests;

/// <summary>
/// The HTML report is handed to people who will act on it and who cannot check it
/// against the source XML. A wrong percentage or a bar that doesn't match its numbers
/// is silent — nothing throws, and the output still looks like a report.
/// </summary>
public class HtmlReportWriterTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string Write(CoverageReport report, double threshold = 80)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-{Guid.NewGuid():N}.html");
        _temp.Add(path);
        HtmlReportWriter.Write(report, path, threshold);
        return File.ReadAllText(path);
    }

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
        root.Add(module); module.Add(space);

        var weak = new CoverageNode { Kind = NodeKind.Class, Name = "Calculator" };
        weak.Add(new CoverageNode
        {
            Kind = NodeKind.Method, Name = "Total()",
            LinesCovered = 2, LinesPartiallyCovered = 1, LinesNotCovered = 7
        });
        space.Add(weak);

        var strong = new CoverageNode { Kind = NodeKind.Class, Name = "RateTable" };
        strong.Add(new CoverageNode { Kind = NodeKind.Method, Name = "Lookup()", LinesCovered = 10 });
        space.Add(strong);

        root.Aggregate();
        return new CoverageReport { SourcePath = @"C:\reports\report.xml", FormatName = "test format", Root = root };
    }

    [Fact]
    public void The_file_is_written_and_is_a_complete_document()
    {
        var html = Write(Sample());

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("charset=\"utf-8\"", html);
    }

    /// <summary>
    /// Self-containment is the point of this export: it gets emailed and attached to
    /// builds, where a CDN reference would render it blank.
    /// </summary>
    [Fact]
    public void Nothing_is_loaded_from_the_network()
    {
        var html = Write(Sample());

        foreach (Match match in Regex.Matches(html, @"(?:src|href)=""([^""]+)"""))
        {
            var target = match.Groups[1].Value;
            Assert.True(target.StartsWith("data:", StringComparison.Ordinal), target);
        }
    }

    [Fact]
    public void The_headline_percentage_matches_the_totals()
    {
        var html = Write(Sample());

        // 12 covered, 1 partial, 7 missed = 20 coverable
        Assert.Contains("60.0%", html.Replace(',', '.'));
        Assert.Contains("lines covered", html);
    }

    /// <summary>Every bar's three segments must account for exactly the whole width.</summary>
    [Fact]
    public void Bar_segments_always_sum_to_one_hundred_percent()
    {
        var html = Write(Sample());
        var bars = Regex.Matches(html, @"<span class=""bar"">(.*?)</span>", RegexOptions.Singleline);

        Assert.NotEmpty(bars);

        foreach (Match bar in bars)
        {
            var widths = Regex.Matches(bar.Groups[1].Value, @"width:([\d.]+)%")
                .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

            if (widths.Count == 0) continue;
            Assert.InRange(widths.Sum(), 99.5, 100.5);
        }
    }

    [Fact]
    public void Types_below_target_appear_in_the_risk_table_and_ones_above_do_not()
    {
        var html = Write(Sample());
        var table = html[html.IndexOf("<table>", StringComparison.Ordinal)..html.IndexOf("</table>", StringComparison.Ordinal)];

        Assert.Contains("Calculator", table);
        Assert.DoesNotContain("RateTable", table);
    }

    [Fact]
    public void A_report_with_nothing_below_target_says_so_instead_of_showing_an_empty_table()
    {
        var html = Write(Sample(), threshold: 10);
        Assert.Contains("at or above", html);
    }

    /// <summary>A type name is untrusted input; it lands in HTML unescaped otherwise.</summary>
    [Fact]
    public void Names_are_html_escaped()
    {
        var report = Sample();
        report.Root.Children[0].Children[0].Children[0].Name = "<script>alert(1)</script>";

        var html = Write(report);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Notes_from_the_report_are_carried_into_the_output()
    {
        var report = Sample();
        report.Notes.Add("42 modules skipped (no_symbols).");

        Assert.Contains("42 modules skipped", Write(report));
    }

    [Fact]
    public void A_type_with_no_instrumented_lines_does_not_divide_by_zero()
    {
        var report = Sample();
        var empty = new CoverageNode { Kind = NodeKind.Class, Name = "Marker" };
        report.Root.Children[0].Children[0].Add(empty);

        var html = Write(report);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("∞", html);
    }
}
