using System;
using System.Collections.Generic;
using System.Linq;
using CoverageInsight.Filtering;
using CoverageInsight.Models;
using Xunit;

namespace CoverageInsight.Tests;

/// <summary>
/// The distinction these tests defend: scoping removes code from the totals, filtering
/// only hides rows. A filter that silently moved the headline, or an exclusion that
/// didn't, would both look entirely plausible on screen.
/// </summary>
public class TreeFilterTests
{
    private static CoverageNode Build()
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report" };

        var app = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var tests = new CoverageNode { Kind = NodeKind.Module, Name = "App.Tests.dll" };
        root.Add(app);
        root.Add(tests);

        var domain = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Domain" };
        var ui = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Ui.Views" };
        app.Add(domain);
        app.Add(ui);

        // 20 covered / 80 missed in the domain
        var calculator = new CoverageNode { Kind = NodeKind.Class, Name = "Calculator" };
        calculator.Add(Method("Total", 20, 0, 30));
        calculator.Add(Method("Discount", 0, 0, 50));
        domain.Add(calculator);

        // fully covered, so "only below target" should drop it
        var table = new CoverageNode { Kind = NodeKind.Class, Name = "RateTable" };
        table.Add(Method("Lookup", 40, 0, 0));
        domain.Add(table);

        var window = new CoverageNode { Kind = NodeKind.Class, Name = "MainWindow" };
        window.Add(Method("OnClick", 0, 0, 100));
        ui.Add(window);

        var suite = new CoverageNode { Kind = NodeKind.Class, Name = "CalculatorTests" };
        suite.Add(Method("Adds", 200, 0, 0));
        tests.Add(new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme.Tests" });
        tests.Children[0].Add(suite);

        root.Aggregate();
        return root;
    }

    private static CoverageNode Method(string name, int covered, int partial, int missed)
        => new()
        {
            Kind = NodeKind.Method,
            Name = name,
            LinesCovered = covered,
            LinesPartiallyCovered = partial,
            LinesNotCovered = missed
        };

    // ---------------- scoping ----------------

    [Fact]
    public void Excluding_a_test_assembly_removes_its_lines_from_the_total()
    {
        var root = Build();
        Assert.Equal(260, root.LinesCovered); // 20 + 40 + 200

        var scoped = TreeFilter.Scope(root, new[] { ExclusionRules.TestAssemblies })!;
        scoped.Aggregate();

        Assert.Equal(60, scoped.LinesCovered);
        Assert.Single(scoped.Children);
    }

    [Fact]
    public void Excluding_ui_re_totals_the_parents()
    {
        var scoped = TreeFilter.Scope(Build(), new[] { ExclusionRules.WpfUi })!;
        scoped.Aggregate();

        // the 100 missed UI lines are gone from the headline
        Assert.Equal(80, scoped.LinesNotCovered);
        Assert.DoesNotContain(scoped.Descendants(), n => n.Name == "MainWindow");
    }

    [Fact]
    public void Scoping_reports_what_it_excluded()
    {
        var excluded = new List<string>();
        TreeFilter.Scope(Build(), new[] { ExclusionRules.TestAssemblies },
                         (node, rule) => excluded.Add($"{rule.Name}:{node.Name}"));

        Assert.Equal(new[] { "test assemblies:App.Tests.dll" }, excluded);
    }

    [Fact]
    public void A_namespace_whose_contents_are_all_excluded_goes_too()
    {
        var scoped = TreeFilter.Scope(Build(), new[] { ExclusionRules.WpfUi })!;
        Assert.DoesNotContain(scoped.Descendants(), n => n.Name == "Acme.Ui.Views");
    }

    [Fact]
    public void No_rules_leaves_the_totals_untouched()
    {
        var scoped = TreeFilter.Scope(Build(), Array.Empty<ExclusionRule>())!;
        scoped.Aggregate();

        Assert.Equal(260, scoped.LinesCovered);
    }

    // ---------------- filtering ----------------

    [Fact]
    public void Filtering_never_changes_the_numbers_on_the_rows_it_keeps()
    {
        var root = Build();
        var module = root.Children[0];

        var filtered = TreeFilter.Filter(module, new FilterSettings { OnlyBelowThreshold = true })!;

        // RateTable is hidden, but the module still reports its real totals
        Assert.Equal(module.LinesCovered, filtered.LinesCovered);
        Assert.Equal(module.CoverableLines, filtered.CoverableLines);
        Assert.DoesNotContain(filtered.Descendants(), n => n.Name == "RateTable");
    }

    [Fact]
    public void Only_below_target_keeps_types_under_the_threshold()
    {
        var filtered = TreeFilter.Filter(Build().Children[0],
            new FilterSettings { OnlyBelowThreshold = true, Threshold = 80 })!;

        var types = filtered.Descendants().Where(n => n.Kind == NodeKind.Class).Select(n => n.Name).ToList();
        Assert.Contains("Calculator", types);
        Assert.Contains("MainWindow", types);
        Assert.DoesNotContain("RateTable", types);
    }

    [Fact]
    public void Hiding_methods_keeps_the_types()
    {
        var filtered = TreeFilter.Filter(Build().Children[0], new FilterSettings { HideMethods = true })!;

        Assert.Contains(filtered.Descendants(), n => n.Kind == NodeKind.Class);
        Assert.DoesNotContain(filtered.Descendants(), n => n.Kind == NodeKind.Method);
    }

    /// <summary>A healthy-looking assembly must not disappear when it holds an unhealthy type.</summary>
    [Fact]
    public void Searching_keeps_the_ancestors_of_a_hit()
    {
        var filtered = TreeFilter.Filter(Build().Children[0], new FilterSettings { Search = "Discount" })!;

        Assert.Equal("App.dll", filtered.Name);
        Assert.Contains(filtered.Descendants(), n => n.Name == "Discount");
        Assert.DoesNotContain(filtered.Descendants(), n => n.Name == "Lookup");
    }

    [Fact]
    public void Searching_matches_the_source_file_too()
    {
        var root = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = "Acme" };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = "Thing", SourceFile = @"C:\src\special.cs" };
        type.Add(Method("Do", 1, 0, 1));
        root.Add(space); space.Add(type);

        Assert.NotNull(TreeFilter.Filter(root, new FilterSettings { Search = "special.cs" }));
        Assert.Null(TreeFilter.Filter(root, new FilterSettings { Search = "nothing-matches" }));
    }

    [Fact]
    public void Searching_expands_the_path_to_the_hits()
    {
        var filtered = TreeFilter.Filter(Build().Children[0], new FilterSettings { Search = "Discount" })!;
        Assert.True(filtered.IsExpanded);
    }
}
