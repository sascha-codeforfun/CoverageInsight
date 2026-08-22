using System;
using System.Collections.Generic;
using System.Linq;

namespace CoverageInsight.Models;

public sealed class CoverageReport
{
    public required string SourcePath { get; init; }
    public required string FormatName { get; init; }
    public required CoverageNode Root { get; init; }
    public DateTime LoadedUtc { get; } = DateTime.UtcNow;
    public List<string> Notes { get; } = new();

    public IEnumerable<CoverageNode> Modules => Root.Children;
    public IEnumerable<CoverageNode> Classes => Root.Descendants().Where(n => n.Kind == NodeKind.Class);
    public IEnumerable<CoverageNode> Methods => Root.Descendants().Where(n => n.Kind == NodeKind.Method);
}
