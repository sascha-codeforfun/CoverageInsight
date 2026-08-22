using System;
using System.Collections.Generic;
using System.Linq;
using CoverageInsight.Filtering;
using CoverageInsight.Models;
using Xunit;

namespace CoverageInsight.Tests;

/// <summary>
/// A false positive here silently deletes real code from the totals, which is the worst
/// failure this app has: the number still looks plausible. The near-miss cases matter
/// more than the hits.
/// </summary>
public class ExclusionRuleTests
{
    private static CoverageNode Type(string ns, string name, string? file = null)
    {
        var root = new CoverageNode { Kind = NodeKind.Report, Name = "report" };
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "App.dll" };
        var space = new CoverageNode { Kind = NodeKind.Namespace, Name = ns };
        var type = new CoverageNode { Kind = NodeKind.Class, Name = name, SourceFile = file };
        root.Add(module); module.Add(space); space.Add(type);
        return type;
    }

    private static bool Excluded(ExclusionRule rule, CoverageNode node)
        => ExclusionRules.FirstMatch(new[] { rule }, node) is not null;

    [Theory]
    [InlineData("Acme.Ui.Views", "MainWindow")]
    [InlineData("Acme.Ui", "InvoiceView")]
    [InlineData("Acme.Ui.Controls", "RibbonBar")]
    public void WpfUi_matches_presentation_types(string ns, string type)
        => Assert.True(Excluded(ExclusionRules.WpfUi, Type(ns, type)));

    /// <summary>ViewModels are the most valuable thing to test in a WPF app.</summary>
    [Fact]
    public void WpfUi_does_not_match_view_models()
        => Assert.False(Excluded(ExclusionRules.WpfUi, Type("Acme.Ui.ViewModels", "InvoiceViewModel")));

    [Theory]
    [InlineData("ReviewService")]
    [InlineData("Overview")]
    [InlineData("PreviewBuilder")]
    public void WpfUi_does_not_match_words_that_merely_contain_view(string type)
        => Assert.False(Excluded(ExclusionRules.WpfUi, Type("Acme.Domain", type)));

    [Fact]
    public void WpfUi_matches_code_behind_by_file_name()
        => Assert.True(Excluded(ExclusionRules.WpfUi,
            Type("Acme.Ui", "Shell", @"C:\src\Acme\Ui\Shell.xaml.cs")));

    [Theory]
    [InlineData("Acme.Http", "TaxRateClient")]
    [InlineData("Acme.Integration", "HttpRetryPolicy")]
    public void Network_matches_wire_facing_types(string ns, string type)
        => Assert.True(Excluded(ExclusionRules.Network, Type(ns, type)));

    [Fact]
    public void Network_does_not_match_ordinary_domain_types()
        => Assert.False(Excluded(ExclusionRules.Network, Type("Acme.Billing", "TaxTable")));

    /// <summary>Assembly names are deliberately not part of the match text.</summary>
    [Fact]
    public void Type_rules_never_match_a_module()
    {
        var module = new CoverageNode { Kind = NodeKind.Module, Name = "Acme.Api.dll" };
        Assert.Null(ExclusionRules.FirstMatch(new[] { ExclusionRules.Network }, module));
    }

    [Theory]
    [InlineData("Acme.Tests.dll", true)]
    [InlineData("Acme.UnitTests.dll", true)]
    [InlineData("Acme.Specs.dll", true)]
    [InlineData("Benchmarks.dll", true)]
    [InlineData("Acme.dll", false)]
    [InlineData("Acme.Testing.dll", false)]
    [InlineData("Acme.TestData.dll", false)]
    public void TestAssemblies_matches_only_whole_name_segments(string module, bool expected)
    {
        var node = new CoverageNode { Kind = NodeKind.Module, Name = module };
        Assert.Equal(expected, ExclusionRules.FirstMatch(new[] { ExclusionRules.TestAssemblies }, node) is not null);
    }

    [Theory]
    [InlineData("System.Text.RegularExpressions.Generated", "UrlRegex_0")]
    [InlineData("Acme.Generated", "ApiClient")]
    [InlineData("Acme.Migrations", "AddUserTable")]
    [InlineData("XamlGeneratedNamespace", "GeneratedInternalTypeHelper")]
    public void GeneratedCode_matches_what_a_generator_wrote(string ns, string type)
        => Assert.True(Excluded(ExclusionRules.GeneratedCode, Type(ns, type)));

    [Fact]
    public void GeneratedCode_matches_designer_files_by_name()
        => Assert.True(Excluded(ExclusionRules.GeneratedCode,
            Type("Acme.Forms", "EditBox", @"C:\src\Acme\EditBox.designer.cs")));

    /// <summary>
    /// The failure that matters: a rule that eats hand-written code makes the headline
    /// look better while hiding work. These all merely sound generated.
    /// </summary>
    [Theory]
    [InlineData("Acme.Reporting", "ReportGenerator")]
    [InlineData("Acme.Domain", "CodeGenerator")]
    [InlineData("Acme.Tools", "GeneratorSettings")]
    [InlineData("Acme.Design", "LayoutEngine")]
    [InlineData("Acme.Migration", "Planner")]
    public void GeneratedCode_does_not_match_code_you_wrote(string ns, string type)
        => Assert.False(Excluded(ExclusionRules.GeneratedCode, Type(ns, type)));

    [Fact]
    public void TestAssemblies_does_not_reach_into_types()
        => Assert.Null(ExclusionRules.FirstMatch(new[] { ExclusionRules.TestAssemblies },
            Type("Acme.Tests", "ConverterTests")));
}

public class RoleRuleTests
{
    [Theory]
    [InlineData("Acme.Domain", "RateConverter", "drift", 1.0)]
    [InlineData("Acme.Domain", "JsonParser", "drift", 1.0)]
    [InlineData("Acme.Data", "InvoiceRepository", "io", 0.7)]
    [InlineData("Acme.Ui", "ReportRenderer", "presentation", 0.4)]
    public void Classify_assigns_the_expected_role(string ns, string type, string role, double weight)
    {
        var (actualRole, actualWeight) = RoleRules.Classify(ns, type);
        Assert.Equal(role, actualRole);
        Assert.Equal(weight, actualWeight);
    }

    /// <summary>
    /// Weighting must only ever demote. An unrecognised type keeping full weight is what
    /// stops a naming heuristic quietly burying real work.
    /// </summary>
    [Fact]
    public void Unmatched_types_keep_full_weight()
    {
        var (role, weight) = RoleRules.Classify("Acme.Whatever", "Thing");
        Assert.Equal("other", role);
        Assert.Equal(1.0, weight);
    }

    [Fact]
    public void No_role_weighs_more_than_unclassified()
    {
        foreach (var rule in new[] { RoleRules.Presentation, RoleRules.Io, RoleRules.Drift })
            Assert.True(rule.Weight <= RoleRules.UnclassifiedWeight, rule.Name);
    }
}
