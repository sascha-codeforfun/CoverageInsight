using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CoverageInsight.Models;

namespace CoverageInsight.Filtering;

/// <summary>
/// A named set of patterns that hides a category of code you don't intend to unit test,
/// so the numbers describe the code you actually want covered.
/// </summary>
public sealed class ExclusionRule
{
    private Regex[]? _compiled;

    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> Patterns { get; init; }

    /// <summary>
    /// Module-scoped rules match an assembly name and drop the whole assembly.
    /// Everything else matches the dotted path from the namespace down.
    /// </summary>
    public bool AppliesToModules { get; init; }

    private Regex[] Compiled => _compiled ??= Patterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
        .ToArray();

    public bool Matches(string text) => Compiled.Any(r => r.IsMatch(text));

    /// <summary>Shown as the checkbox tooltip so the rules aren't a black box.</summary>
    public string Help => Summary + "\n\nPatterns:\n  " + string.Join("\n  ", Patterns);
}

public static class ExclusionRules
{
    /// <summary>
    /// Wire-facing code: clients, transports, generated service references. Testing this
    /// needs a server or a mock of one, so it rarely belongs in the same queue as domain logic.
    /// </summary>
    public static readonly ExclusionRule Network = new()
    {
        Name = "network",
        Summary = "Hides HTTP/socket clients, transports and generated service references.",
        Patterns = new[]
        {
            // namespace segments
            @"(^|\.)(Net|Network|Networking|Http|Rest|Grpc|Soap|Wcf|SignalR|Sockets?|Transport|Remoting|Communication|Connectivity|WebService|WebServices|ServiceReference|ConnectedServices)(\.|$)",
            // type-name suffixes
            @"\w*(HttpClient|RestClient|ApiClient|ServiceClient|WebClient|SoapClient|GrpcClient|HttpHandler|HttpMessageHandler|Downloader|Uploader|Endpoint|Gateway|WebHook|SocketListener|TcpListener|TcpClient)\b",
            // anything obviously wire-shaped
            @"\bHttp[A-Z]\w*",
            // generated WCF / service-reference files
            @"Reference\.cs\b",
            @"\.svcmap\b"
        }
    };

    /// <summary>
    /// Presentation code: windows, views, code-behind and XAML-generated partials.
    /// Note that ViewModels are deliberately *not* matched — they're prime test targets.
    /// </summary>
    public static readonly ExclusionRule WpfUi = new()
    {
        Name = "WPF UI",
        Summary = "Hides windows, views, code-behind and XAML-generated code. ViewModels are kept, "
                + "since they're usually the most valuable thing to test in a WPF app.",
        Patterns = new[]
        {
            // code-behind and generated partials
            @"\.xaml\.cs\b",
            @"\.g\.i?\.cs\b",
            @"\.baml\b",
            // namespace segments
            @"(^|\.)(Views|Windows|Controls|Dialogs|Pages|Screens|Xaml|Themes|Styles|Resources)(\.|$)",
            // folder names in the source path
            @"[\\/](Views|Windows|Controls|Dialogs|Pages|Screens|Themes)[\\/]",
            // type-name suffixes; View is matched but ViewModel is not
            @"\w*(Window|Page|UserControl|Dialog|Popup|Flyout|Adorner|Shell)\b",
            // (?-i:) keeps the capital V, so Review / Overview / Preview stay in scope
            @"\b\w*(?-i:View)(?!Model)\b",
            // XAML plumbing
            @"\bInitializeComponent\b",
            @"\bIComponentConnector\b",
            @"\bXamlGeneratedNamespace\b",
            @"\bGeneratedInternalTypeHelper\b"
        }
    };

    /// <summary>
    /// Code a generator wrote. Testing it means testing the generator — someone else's,
    /// already tested by them — and on a project that leans on source generators it can
    /// be a sizeable share of the coverable lines, dragging the headline in both
    /// directions without ever describing work you could do.
    ///
    /// Not matched: your own partial that a generator completes. Only the generated
    /// half carries these markers.
    /// </summary>
    public static readonly ExclusionRule GeneratedCode = new()
    {
        Name = "generated code",
        Summary = "Hides source-generated and tool-generated code — regex generators, "
                + "designers, resource and settings classes, service references.",
        Patterns = new[]
        {
            // namespaces source generators emit into
            @"(^|\.)(Generated|CodeDom|Designer|Migrations)(\.|$)",
            @"\.RegularExpressions\.Generated(\.|$)",
            // generated file names
            @"\.g\.i?\.cs\b",
            @"\.designer\.cs\b",
            @"\.generated\.cs\b",
            // the classes tooling emits
            @"\w*(Resources|Settings)\.Designer\b",
            @"\bGeneratedRegex\w*",
            @"\bGeneratedInternalTypeHelper\b",
            @"\bXamlGeneratedNamespace\b"
        }
    };

    /// <summary>
    /// Test assemblies are instrumented by the collector like any other, and their coverage
    /// is near-total by construction — a test project runs its own code. Summing them into
    /// the headline pulls it toward the tests rather than the code under test, which is
    /// never the thing being asked about.
    /// </summary>
    public static readonly ExclusionRule TestAssemblies = new()
    {
        Name = "test assemblies",
        AppliesToModules = true,
        Summary = "Drops assemblies whose name marks them as a test or benchmark project, "
                + "so the headline describes the code under test rather than the tests.",
        Patterns = new[]
        {
            @"(^|\.)(Tests?|UnitTests?|IntegrationTests?|AcceptanceTests?|FunctionalTests?|Specs?|E2E|Benchmarks?)(\.dll|\.exe)?$"
        }
    };

    /// <summary>
    /// The text a rule is matched against: the dotted path from the namespace down,
    /// plus the source file. Assembly names are left out on purpose — one assembly called
    /// Contoso.Api.dll shouldn't disappear wholesale just because of its name.
    /// </summary>
    public static string TextFor(CoverageNode node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null && n.Kind is not (NodeKind.Report or NodeKind.Module); n = n.Parent)
            parts.Insert(0, n.Name);

        var text = string.Join(".", parts);
        return string.IsNullOrEmpty(node.SourceFile) ? text : text + " " + node.SourceFile;
    }

    public static ExclusionRule? FirstMatch(IReadOnlyList<ExclusionRule> rules, CoverageNode node)
    {
        if (rules.Count == 0 || node.Kind == NodeKind.Report)
            return null;

        if (node.Kind == NodeKind.Module)
            return rules.FirstOrDefault(r => r.AppliesToModules && r.Matches(node.Name));

        var text = TextFor(node);
        foreach (var rule in rules)
            if (!rule.AppliesToModules && rule.Matches(text))
                return rule;

        return null;
    }
}
