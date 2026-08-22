using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoverageInsight.Models;
using CoverageInsight.Parsing;
using Xunit;

namespace CoverageInsight.Tests;

public class CoverageParserTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string File_(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
            try { File.Delete(path); } catch { /* best effort */ }
    }

    private static CoverageNode Find(CoverageReport report, NodeKind kind, string name)
        => report.Root.Descendants().First(n => n.Kind == kind && n.Name == name);

    // ---------------- Microsoft XML ----------------

    private const string MicrosoftXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <results>
          <modules>
            <module name="App.dll" path="App.dll"
                    lines_covered="6" lines_partially_covered="1" lines_not_covered="5"
                    blocks_covered="9" blocks_not_covered="4">
              <functions>
                <function name="Total(Invoice)" namespace="Acme.Billing" type_name="Calculator"
                          lines_covered="6" lines_partially_covered="1" lines_not_covered="2"
                          blocks_covered="9" blocks_not_covered="1">
                  <ranges>
                    <range start_line="10" end_line="10" covered="yes" source_id="0" />
                    <range start_line="14" end_line="15" covered="no" source_id="0" />
                  </ranges>
                </function>
                <function name="MoveNext()" namespace="Acme.Billing" type_name="Calculator.&lt;RunAsync&gt;d__4"
                          lines_covered="0" lines_partially_covered="0" lines_not_covered="3">
                  <ranges>
                    <range start_line="30" end_line="32" covered="no" source_id="0" />
                  </ranges>
                </function>
              </functions>
              <source_files>
                <source_file id="0" path="C:\src\Acme\Calculator.cs" />
              </source_files>
            </module>
          </modules>
          <skipped_modules>
            <skipped_module name="system.private.corelib.dll" reason="no_symbols" />
          </skipped_modules>
        </results>
        """;

    [Fact]
    public void Microsoft_xml_is_detected()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));
        Assert.Contains("Microsoft XML", report.FormatName);
    }

    [Fact]
    public void Microsoft_xml_totals_roll_up_from_the_methods()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));

        Assert.Equal(6, report.Root.LinesCovered);
        Assert.Equal(1, report.Root.LinesPartiallyCovered);
        Assert.Equal(5, report.Root.LinesNotCovered);
        Assert.Equal(12, report.Root.CoverableLines);
    }

    /// <summary>Counts alone can't say which lines are dark; the ranges are the payload.</summary>
    [Fact]
    public void Microsoft_xml_captures_missed_line_numbers()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));
        var method = Find(report, NodeKind.Method, "Total(Invoice)");

        Assert.Equal("14-15", method.MissedRangeText);
    }

    /// <summary>A state machine must land under the type a person wrote, not beside it.</summary>
    [Fact]
    public void Microsoft_xml_groups_generated_types_under_their_declaring_type()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));
        var types = report.Root.Descendants().Where(n => n.Kind == NodeKind.Class).ToList();

        Assert.Single(types);
        Assert.Equal("Calculator", types[0].Name);
        Assert.Equal(2, types[0].Children.Count);
    }

    [Fact]
    public void Microsoft_xml_reports_skipped_modules_as_a_note()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));
        Assert.Contains(report.Notes, n => n.Contains("skipped"));
    }

    [Fact]
    public void Microsoft_xml_records_the_source_file()
    {
        var report = CoverageParser.Load(File_("ms.xml", MicrosoftXml));
        Assert.EndsWith("Calculator.cs", Find(report, NodeKind.Method, "Total(Invoice)").SourceFile);
    }

    // ---------------- legacy CoverageDSPriv ----------------

    // Coverage: 0 = covered, 1 = partially covered, 2 = not covered.
    private const string LegacyXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <CoverageDSPriv>
          <Module>
            <ModuleName>App.dll</ModuleName>
            <NamespaceTable>
              <NamespaceName>Acme.Billing</NamespaceName>
              <Class>
                <ClassName>Calculator</ClassName>
                <Method>
                  <MethodName>Total</MethodName>
                  <MethodFullName>Total(Invoice)</MethodFullName>
                  <LinesCovered>4</LinesCovered>
                  <LinesPartiallyCovered>1</LinesPartiallyCovered>
                  <LinesNotCovered>2</LinesNotCovered>
                  <Lines>
                    <LnStart>10</LnStart><LnEnd>10</LnEnd><Coverage>0</Coverage><SourceFileID>1</SourceFileID>
                  </Lines>
                  <Lines>
                    <LnStart>21</LnStart><LnEnd>22</LnEnd><Coverage>2</Coverage><SourceFileID>1</SourceFileID>
                  </Lines>
                </Method>
              </Class>
            </NamespaceTable>
          </Module>
          <SourceFileNames>
            <SourceFileID>1</SourceFileID>
            <SourceFileName>C:\src\Acme\Calculator.cs</SourceFileName>
          </SourceFileNames>
        </CoverageDSPriv>
        """;

    [Fact]
    public void Legacy_xml_is_detected_and_rolls_up()
    {
        var report = CoverageParser.Load(File_("legacy.coveragexml", LegacyXml));

        Assert.Contains("Visual Studio", report.FormatName);
        Assert.Equal(4, report.Root.LinesCovered);
        Assert.Equal(2, report.Root.LinesNotCovered);
    }

    /// <summary>
    /// The Coverage element is an enum, and the mapping is an assumption worth pinning:
    /// 2 means not covered. Reading it as anything else corrupts every missed range.
    /// </summary>
    [Fact]
    public void Legacy_xml_treats_coverage_2_as_missed()
    {
        var report = CoverageParser.Load(File_("legacy.coveragexml", LegacyXml));
        Assert.Equal("21-22", Find(report, NodeKind.Method, "Total(Invoice)").MissedRangeText);
    }

    // ---------------- Cobertura ----------------

    private const string CoberturaXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.6">
          <packages>
            <package name="App">
              <classes>
                <class name="Acme.Billing.Calculator" filename="Acme/Calculator.cs">
                  <methods>
                    <method name="Total" signature="(Invoice)">
                      <lines>
                        <line number="10" hits="3" />
                        <line number="11" hits="0" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="3" />
                    <line number="11" hits="0" />
                    <line number="12" hits="1" branch="true" condition-coverage="50% (1/2)" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void Cobertura_is_detected_and_splits_the_namespace_from_the_type()
    {
        var report = CoverageParser.Load(File_("cob.xml", CoberturaXml));

        Assert.Contains("Cobertura", report.FormatName);
        Assert.Equal("Acme.Billing", Find(report, NodeKind.Namespace, "Acme.Billing").Name);
        Assert.Equal("Calculator", Find(report, NodeKind.Class, "Calculator").Name);
    }

    [Fact]
    public void Cobertura_counts_a_half_covered_branch_as_partial()
    {
        var report = CoverageParser.Load(File_("cob.xml", CoberturaXml));
        var type = Find(report, NodeKind.Class, "Calculator");

        Assert.Equal(1, type.LinesCovered);
        Assert.Equal(1, type.LinesPartiallyCovered);
        Assert.Equal(1, type.LinesNotCovered);
        Assert.Equal(1, type.BranchesCovered);
        Assert.Equal(2, type.BranchesTotal);
    }

    /// <summary>
    /// Cobertura reports type totals directly and they can include members that never
    /// appear under a method, so the type's own numbers win over the method sum.
    /// </summary>
    [Fact]
    public void Cobertura_keeps_the_type_level_totals()
        => Assert.Equal(3, CoverageParser.Load(File_("cob.xml", CoberturaXml))
                                         .Root.CoverableLines);

    // ---------------- failure modes ----------------

    [Fact]
    public void A_binary_coverage_file_explains_how_to_convert_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-{Guid.NewGuid():N}.coverage");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });
        _temp.Add(path);

        var error = Assert.Throws<InvalidDataException>(() => CoverageParser.Load(path));
        Assert.Contains("dotnet-coverage", error.Message);
    }

    [Fact]
    public void An_unknown_root_element_names_what_is_supported()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => CoverageParser.Load(File_("other.xml", "<report><thing /></report>")));

        Assert.Contains("report", error.Message);
        Assert.Contains("results", error.Message);
    }

    [Fact]
    public void Malformed_xml_is_reported_as_such()
        => Assert.Throws<InvalidDataException>(
            () => CoverageParser.Load(File_("bad.xml", "<results><modules>")));

    [Fact]
    public void A_missing_file_throws_file_not_found()
        => Assert.Throws<FileNotFoundException>(
            () => CoverageParser.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.xml")));
}
