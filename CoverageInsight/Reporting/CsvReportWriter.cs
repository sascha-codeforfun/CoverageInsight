using System.Globalization;
using System.IO;
using System.Text;
using CoverageInsight.Models;

namespace CoverageInsight.Reporting;

/// <summary>Flattens the tree to one row per node so it can go into a spreadsheet or a build gate.</summary>
public static class CsvReportWriter
{
    public static void Write(CoverageReport report, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Level,Path,LinesCovered,LinesPartial,LinesNotCovered,CoverableLines,LinePercent," +
                      "BlocksCovered,BlocksNotCovered,BranchesCovered,BranchesTotal,SourceFile,FirstLine");

        foreach (var node in report.Root.Descendants())
        {
            sb.Append(node.Kind).Append(',');
            sb.Append(Quote(node.FullPath)).Append(',');
            sb.Append(node.LinesCovered).Append(',');
            sb.Append(node.LinesPartiallyCovered).Append(',');
            sb.Append(node.LinesNotCovered).Append(',');
            sb.Append(node.CoverableLines).Append(',');
            sb.Append(node.LinePercent.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(node.BlocksCovered).Append(',');
            sb.Append(node.BlocksNotCovered).Append(',');
            sb.Append(node.BranchesCovered).Append(',');
            sb.Append(node.BranchesTotal).Append(',');
            sb.Append(Quote(node.SourceFile ?? string.Empty)).Append(',');
            sb.Append(node.FirstLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            sb.AppendLine();
        }

        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";
}
