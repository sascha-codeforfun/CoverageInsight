using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CoverageInsight.Models;

public enum NodeKind
{
    Report,
    Module,
    Namespace,
    Class,
    Method
}

/// <summary>
/// One node of the coverage tree. The same type is used at every level so that
/// rollups, filtering and rendering only have to be written once.
/// </summary>
public sealed class CoverageNode : INotifyPropertyChanged
{
    public NodeKind Kind { get; init; }
    public string Name { get; set; } = string.Empty;
    public CoverageNode? Parent { get; private set; }
    public List<CoverageNode> Children { get; } = new();

    /// <summary>
    /// The type name exactly as the report gave it, before generated nested types were
    /// normalised away. A state machine arrives as "Worker.&lt;RunAsync&gt;d__12" with a
    /// method called MoveNext, and normalising the type for grouping destroys the only
    /// record of which method it belongs to. Folding needs the original.
    /// </summary>
    public string? DeclaringType { get; set; }

    /// <summary>Where the source lives, when the report tells us.</summary>
    public string? SourceFile { get; set; }
    public int? FirstLine { get; set; }

    /// <summary>
    /// True when this node's own numbers are authoritative and must not be
    /// recomputed from its children (Cobertura reports class totals directly,
    /// and those totals include members that never show up under a method).
    /// </summary>
    public bool SelfMeasured { get; set; }

    public int LinesCovered { get; set; }
    public int LinesPartiallyCovered { get; set; }
    public int LinesNotCovered { get; set; }
    public int BlocksCovered { get; set; }
    public int BlocksNotCovered { get; set; }
    public int BranchesCovered { get; set; }
    public int BranchesTotal { get; set; }

    // ---- derived numbers -------------------------------------------------

    public int CoverableLines => LinesCovered + LinesPartiallyCovered + LinesNotCovered;
    public int UntestedLines => LinesNotCovered + LinesPartiallyCovered;
    public bool HasData => CoverableLines > 0;

    public double LinePercent => CoverableLines == 0 ? 0d : 100d * LinesCovered / CoverableLines;

    public int TotalBlocks => BlocksCovered + BlocksNotCovered;
    public bool HasBlocks => TotalBlocks > 0;
    public double BlockPercent => TotalBlocks == 0 ? 0d : 100d * BlocksCovered / TotalBlocks;

    public bool HasBranches => BranchesTotal > 0;
    public double BranchPercent => BranchesTotal == 0 ? 0d : 100d * BranchesCovered / BranchesTotal;

    // Ribbon segments (0..1). With no data at all we paint one flat grey bar.
    public double BarCovered => CoverableLines == 0 ? 0d : (double)LinesCovered / CoverableLines;
    public double BarPartial => CoverableLines == 0 ? 0d : (double)LinesPartiallyCovered / CoverableLines;
    public double BarUncovered => CoverableLines == 0 ? 1d : (double)LinesNotCovered / CoverableLines;

    public string KindBadge => Kind switch
    {
        NodeKind.Report => "RPT",
        NodeKind.Module => "ASM",
        NodeKind.Namespace => "NS",
        NodeKind.Class => "CLS",
        _ => "FN"
    };

    public string PercentText => HasData ? LinePercent.ToString("0.0") + "%" : "n/a";

    public string CountsText => HasData
        ? $"{LinesCovered:N0} / {CoverableLines:N0} lines" +
          (LinesPartiallyCovered > 0 ? $"  ·  {LinesPartiallyCovered:N0} partial" : string.Empty) +
          (LinesNotCovered > 0 ? $"  ·  {LinesNotCovered:N0} missed" : string.Empty)
        : "no instrumented lines";

    public string ExtraText
    {
        get
        {
            var parts = new List<string>();
            if (HasBlocks) parts.Add($"blocks {BlockPercent:0.0}%");
            if (HasBranches) parts.Add($"branches {BranchPercent:0.0}% ({BranchesCovered}/{BranchesTotal})");
            if (Children.Count > 0) parts.Add($"{Children.Count} child items");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Dotted path from the module down, used in exports and the hotspot list.</summary>
    public string FullPath
    {
        get
        {
            var parts = new List<string>();
            for (var n = this; n is not null && n.Kind != NodeKind.Report; n = n.Parent)
                parts.Insert(0, n.Name);
            return string.Join(" › ", parts);
        }
    }

    public string Location => SourceFile is null
        ? string.Empty
        : FirstLine is > 0 ? $"{SourceFile}:{FirstLine}" : SourceFile;

    // ---- tree plumbing ---------------------------------------------------

    public void Add(CoverageNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public IEnumerable<CoverageNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grandChild in child.Descendants())
                yield return grandChild;
        }
    }

    // ---- missed line numbers --------------------------------------------

    private List<int>? _missedLines;

    /// <summary>
    /// Line numbers that never executed. Counts alone can't say *which* lines are dark,
    /// and that's the one thing a reader can't recover from a percentage.
    /// </summary>
    public void AddMissedLines(int startLine, int endLine)
    {
        if (startLine <= 0) return;
        if (endLine < startLine) endLine = startLine;
        if (endLine - startLine > 5000) endLine = startLine; // guard against junk spans

        _missedLines ??= new List<int>();
        for (var line = startLine; line <= endLine; line++)
            _missedLines.Add(line);
    }

    public bool HasMissedLines => _missedLines is { Count: > 0 };

    /// <summary>The raw missed line numbers, so callers that merge nodes can pool them.</summary>
    public IReadOnlyList<int> MissedLines => _missedLines ?? (IReadOnlyList<int>)System.Array.Empty<int>();

    /// <summary>Collapsed form, e.g. "118-121;129-139;155".</summary>
    public string MissedRangeText => _missedLines is null ? string.Empty : LineRanges.Collapse(_missedLines);

    /// <summary>Post-order rollup of every counter that isn't self-measured.</summary>
    public void Aggregate()
    {
        foreach (var child in Children)
            child.Aggregate();

        if (Children.Count == 0 || SelfMeasured)
            return;

        LinesCovered = Children.Sum(c => c.LinesCovered);
        LinesPartiallyCovered = Children.Sum(c => c.LinesPartiallyCovered);
        LinesNotCovered = Children.Sum(c => c.LinesNotCovered);
        BlocksCovered = Children.Sum(c => c.BlocksCovered);
        BlocksNotCovered = Children.Sum(c => c.BlocksNotCovered);
        BranchesCovered = Children.Sum(c => c.BranchesCovered);
        BranchesTotal = Children.Sum(c => c.BranchesTotal);
    }

    public void SortRecursive()
    {
        Children.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        foreach (var child in Children)
            child.SortRecursive();
    }

    /// <summary>
    /// Copy without children. The filtered view tree pins the totals (selfMeasured: true)
    /// so hiding rows never changes the numbers; the scoped tree keeps the original flag
    /// so removing excluded code genuinely re-totals the parents.
    /// </summary>
    public CoverageNode CloneShallow(bool pinTotals = true) => new()
    {
        Kind = Kind,
        Name = Name,
        DeclaringType = DeclaringType,
        SourceFile = SourceFile,
        FirstLine = FirstLine,
        SelfMeasured = pinTotals || SelfMeasured,
        LinesCovered = LinesCovered,
        LinesPartiallyCovered = LinesPartiallyCovered,
        LinesNotCovered = LinesNotCovered,
        BlocksCovered = BlocksCovered,
        BlocksNotCovered = BlocksNotCovered,
        BranchesCovered = BranchesCovered,
        BranchesTotal = BranchesTotal,
        _missedLines = _missedLines
    };

    // ---- view state ------------------------------------------------------

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public void SetExpandedRecursive(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var child in Children)
            child.SetExpandedRecursive(expanded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
