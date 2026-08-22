using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CoverageInsight.Filtering;
using CoverageInsight.Models;
using CoverageInsight.Parsing;
using CoverageInsight.Reporting;
using Microsoft.Win32;

namespace CoverageInsight.ViewModels;

public sealed class MainViewModel : Observable
{
    private CoverageReport? _report;
    private CoverageNode? _scoped;
    private string _search = string.Empty;
    private double _threshold = 80;
    private bool _onlyBelowThreshold;
    private bool _hideMethods;
    private bool _hideNetwork;
    private bool _hideWpfUi;
    private bool _hideTestAssemblies = true;
    private bool _hideGenerated = true;
    private bool _foldGenerated = true;
    private CoverageNode? _unfilteredInScope;
    private readonly Dictionary<string, (int Types, int Lines)> _excluded = new();
    private readonly List<string> _excludedModules = new();
    private string _status = "Open a coverage XML file, or drop one onto the window.";

    public MainViewModel()
    {
        OpenCommand = new RelayCommand(Open);
        ReloadCommand = new RelayCommand(Reload, () => _report is not null);
        ExportHtmlCommand = new RelayCommand(ExportHtml, () => _report is not null);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _report is not null);
        ExportAiCsvCommand = new RelayCommand(ExportAiCsv, () => _report is not null);
        CopyDigestCommand = new RelayCommand(CopyDigest, () => _report is not null);
        SaveDigestCommand = new RelayCommand(SaveDigest, () => _report is not null);
        ExpandAllCommand = new RelayCommand(() => SetExpanded(true), () => Tree.Count > 0);
        CollapseAllCommand = new RelayCommand(() => SetExpanded(false), () => Tree.Count > 0);
        ClearSearchCommand = new RelayCommand(() => Search = string.Empty);
        OpenInIdeCommand = new RelayCommand(OpenInIde, () => !string.IsNullOrEmpty(Selected?.SourceFile));
    }

    public ObservableCollection<CoverageNode> Tree { get; } = new();
    public ObservableCollection<CoverageNode> Hotspots { get; } = new();

    public RelayCommand OpenCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportAiCsvCommand { get; }
    public RelayCommand CopyDigestCommand { get; }
    public RelayCommand SaveDigestCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand OpenInIdeCommand { get; }

    // ---- bound state -----------------------------------------------------

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Rebuild(); }
    }

    public double Threshold
    {
        get => _threshold;
        set { if (Set(ref _threshold, Math.Clamp(Math.Round(value), 0, 100))) Rebuild(); }
    }

    public bool OnlyBelowThreshold
    {
        get => _onlyBelowThreshold;
        set { if (Set(ref _onlyBelowThreshold, value)) Rebuild(); }
    }

    public bool HideMethods
    {
        get => _hideMethods;
        set { if (Set(ref _hideMethods, value)) Rebuild(); }
    }

    public bool HideNetwork
    {
        get => _hideNetwork;
        set { if (Set(ref _hideNetwork, value)) Rebuild(); }
    }

    public bool HideWpfUi
    {
        get => _hideWpfUi;
        set { if (Set(ref _hideWpfUi, value)) Rebuild(); }
    }

    public bool HideTestAssemblies
    {
        get => _hideTestAssemblies;
        set { if (Set(ref _hideTestAssemblies, value)) Rebuild(); }
    }

    /// <summary>Affects the AI CSV only; the tree always shows real members.</summary>
    public bool FoldGenerated
    {
        get => _foldGenerated;
        set => Set(ref _foldGenerated, value);
    }

    public bool HideGenerated
    {
        get => _hideGenerated;
        set { if (Set(ref _hideGenerated, value)) Rebuild(); }
    }

    public string TestAssemblyRuleHelp => ExclusionRules.TestAssemblies.Help;
    public string GeneratedRuleHelp => ExclusionRules.GeneratedCode.Help;
    public string RoleRuleHelp => RoleRules.Help;

    public string FoldRuleHelp =>
        "Affects the CSV for AI only.\n\n"
        + "On: async bodies, iterators, lambdas and local functions are folded into the method "
        + "that declared them, so a method whose uncovered lines are spread across a dozen "
        + "generated members reports as one row and can't hide below the ranking. The "
        + "'generated' column records what was folded; the missed line ranges are merged, not lost.\n\n"
        + "Off: every generated member keeps its own row and its own IL name.";
    public string NetworkRuleHelp => ExclusionRules.Network.Help;
    public string WpfUiRuleHelp => ExclusionRules.WpfUi.Help;

    /// <summary>Says exactly what the toggles removed, so the headline number can be trusted.</summary>
    public string ExclusionSummary
    {
        get
        {
            if (_excluded.Count == 0) return string.Empty;

            var types = _excluded.Values.Sum(v => v.Types);
            var lines = _excluded.Values.Sum(v => v.Lines);
            var breakdown = string.Join(", ", _excluded
                .OrderByDescending(kv => kv.Value.Types)
                .Select(kv => $"{kv.Key} {kv.Value.Types:N0}"));

            return $"excluded {types:N0} types · {lines:N0} lines  ({breakdown})";
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private CoverageNode? _selected;
    public CoverageNode? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(HasSelection)); Raise(nameof(SelectionDetail)); } }
    }

    public bool HasSelection => _selected is not null;

    public string SelectionDetail
    {
        get
        {
            if (_selected is null) return string.Empty;
            var extra = _selected.ExtraText;
            var location = string.IsNullOrEmpty(_selected.Location) ? string.Empty : "  ·  " + _selected.Location;
            return $"{_selected.FullPath}\n{_selected.CountsText}" +
                   (extra.Length > 0 ? "  ·  " + extra : string.Empty) + location;
        }
    }

    // ---- summary (all of it reads the scoped tree, i.e. after exclusions) ----

    public bool HasReport => _report is not null;
    public string FileName => _report is null ? "No file loaded" : Path.GetFileName(_report.SourcePath);
    public string FormatName => _report?.FormatName ?? string.Empty;
    public CoverageNode? Totals => _scoped;

    private IEnumerable<CoverageNode> ScopedClasses =>
        _scoped?.Descendants().Where(n => n.Kind == NodeKind.Class) ?? Enumerable.Empty<CoverageNode>();

    public string OverallPercentText => _scoped is null ? "—" : _scoped.LinePercent.ToString("0.0") + "%";
    public string LinesText => _scoped is null
        ? "—"
        : $"{_scoped.LinesCovered:N0} of {_scoped.CoverableLines:N0}";
    public string MissedText => _scoped is null ? "—" : _scoped.LinesNotCovered.ToString("N0");
    public string PartialText => _scoped is null ? "—" : _scoped.LinesPartiallyCovered.ToString("N0");
    public string BranchText => _scoped is null || !_scoped.HasBranches
        ? "n/a"
        : _scoped.BranchPercent.ToString("0.0") + "%";
    public string BlockText => _scoped is null || !_scoped.HasBlocks
        ? "n/a"
        : _scoped.BlockPercent.ToString("0.0") + "%";
    public string RiskCountText => _scoped is null
        ? "—"
        : ScopedClasses.Count(c => c.HasData && c.LinePercent < _threshold).ToString("N0");
    public string RiskCaption => $"types under {_threshold:0}%";
    public string ScopeText => _scoped is null
        ? string.Empty
        : $"{_scoped.Children.Count:N0} assemblies · {ScopedClasses.Count():N0} types · " +
          $"{_scoped.Descendants().Count(n => n.Kind == NodeKind.Method):N0} methods";

    // ---- actions ---------------------------------------------------------

    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a coverage report",
            Filter = "Coverage XML (*.xml;*.coveragexml;*.cobertura.xml)|*.xml;*.coveragexml;*.cobertura.xml|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            Load(dialog.FileName);
    }

    private void Reload()
    {
        if (_report is not null)
            Load(_report.SourcePath);
    }

    public void Load(string path)
    {
        try
        {
            var report = CoverageParser.Load(path);
            _report = report;
            Selected = null;
            Rebuild();

            var note = report.Notes.Count > 0 ? "  ·  " + string.Join(" ", report.Notes) : string.Empty;
            Status = $"Loaded {Path.GetFileName(path)} — {report.FormatName}{note}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Can't read that file", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = "Could not read " + Path.GetFileName(path) + ".";
        }
    }

    private void ExportHtml()
    {
        if (_report is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save HTML report",
            Filter = "HTML report (*.html)|*.html",
            FileName = Path.GetFileNameWithoutExtension(_report.SourcePath) + "-coverage.html"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            HtmlReportWriter.Write(ExportSource(), dialog.FileName, _threshold);
            Status = "Saved " + dialog.FileName;
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportCsv()
    {
        if (_report is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_report.SourcePath) + "-coverage.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvReportWriter.Write(ExportSource(), dialog.FileName);
            Status = "Saved " + dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Exports match what's on screen. Exclusions always apply (they change the totals);
    /// the display filters apply too when ExportsFollowFilters is on, which is the point
    /// when the output is going into a model's context rather than an archive.
    /// </summary>
    private CoverageReport ExportSource(bool keepMethods = false)
    {
        var report = _report!;
        var root = _scoped ?? report.Root;

        if (ExportsFollowFilters && DisplayFilterActive)
        {
            // Root keeps the real totals; only the rows beneath it are the visible ones.
            var visible = root.CloneShallow();
            var settings = CurrentFilters with { HideMethods = _hideMethods && !keepMethods };

            foreach (var module in root.Children)
            {
                var kept = TreeFilter.Filter(module, settings);
                if (kept is not null)
                    visible.Add(kept);
            }
            root = visible;
        }

        if (ReferenceEquals(root, report.Root))
            return report;

        var scoped = new CoverageReport
        {
            SourcePath = report.SourcePath,
            FormatName = report.FormatName,
            Root = root
        };
        scoped.Notes.AddRange(report.Notes);
        if (_excluded.Count > 0)
            scoped.Notes.Add("Filtered report — " + ExclusionSummary + ".");
        return scoped;
    }

    private bool DisplayFilterActive =>
        _onlyBelowThreshold || _hideMethods || !string.IsNullOrWhiteSpace(_search);

    /// <summary>Plain-language list of what's currently narrowing the output.</summary>
    private string FilterNote()
    {
        var parts = new List<string>();
        if (_excluded.Count > 0) parts.Add(ExclusionSummary);
        if (ExportsFollowFilters)
        {
            if (_onlyBelowThreshold) parts.Add("only types under target");
            if (_hideMethods) parts.Add("methods hidden");
            if (!string.IsNullOrWhiteSpace(_search)) parts.Add($"matching \"{_search}\"");
        }
        return string.Join("; ", parts);
    }

    /// <summary>Names the assemblies in and out of scope, so a reader can spot a wrong scope on sight.</summary>
    private string ScopeNote()
    {
        var kept = _scoped is null ? Array.Empty<string>() : _scoped.Children.Select(m => m.Name).ToArray();
        var note = kept.Length == 0 ? "no assemblies" : string.Join(" · ", kept);

        if (_excludedModules.Count > 0)
            note += $" · excluded ({string.Join(", ", _excludedModules)})";

        return note;
    }

    private void ExportAiCsv()
    {
        if (_report is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save CSV for AI",
            Filter = "CSV (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_report.SourcePath) + "-coverage-ai.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // keepMethods: "Types only" is a reading convenience, but a method-level
            // file with no methods in it would be worse than useless.
            var options = new AiCsvOptions
            {
                FoldGenerated = _foldGenerated,
                FilterNote = FilterNote(),
                ScopeNote = ScopeNote()
            };
            var text = AiCsvWriter.Build(ExportSource(keepMethods: true),
                                         _unfilteredInScope ?? _report.Root, options);
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));

            var rows = text.Split('\n').Length - 4;
            Status = $"Saved {dialog.FileName} — {rows:N0} rows, roughly " +
                     $"{ContextDigestWriter.EstimateTokens(text):N0} tokens.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool _exportsFollowFilters = true;

    public bool ExportsFollowFilters
    {
        get => _exportsFollowFilters;
        set => Set(ref _exportsFollowFilters, value);
    }

    private void CopyDigest()
    {
        if (_report is null) return;

        var digest = ContextDigestWriter.Build(ExportSource(), _threshold, FilterNote());

        try
        {
            Clipboard.SetText(digest);
            Status = $"Digest copied — {digest.Length:N0} chars, roughly " +
                     $"{ContextDigestWriter.EstimateTokens(digest):N0} tokens.";
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource; another process can hold it open.
            MessageBox.Show("Couldn't reach the clipboard: " + ex.Message + "\n\nSave the digest instead.",
                "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveDigest()
    {
        if (_report is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save digest",
            Filter = "Markdown (*.md)|*.md",
            FileName = Path.GetFileNameWithoutExtension(_report.SourcePath) + "-digest.md"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var digest = ContextDigestWriter.Build(ExportSource(), _threshold, FilterNote());
            File.WriteAllText(dialog.FileName, digest, new UTF8Encoding(false));
            Status = $"Saved {dialog.FileName} — roughly " +
                     $"{ContextDigestWriter.EstimateTokens(digest):N0} tokens.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenInIde()
    {
        Status = IdeLauncher.Open(Selected?.SourceFile);
    }

    private void SetExpanded(bool expanded)
    {
        foreach (var node in Tree)
            node.SetExpandedRecursive(expanded);
    }

    // ---- filtered view tree ---------------------------------------------

    private void Rebuild()
    {
        PercentToBrushConverter.Threshold = _threshold;

        Tree.Clear();
        Hotspots.Clear();
        _excluded.Clear();
        _excludedModules.Clear();
        _scoped = null;
        _unfilteredInScope = null;

        if (_report is not null)
        {
            // Pass 1: drop excluded code entirely and re-total what's left.
            var rules = new List<ExclusionRule>();
            if (_hideTestAssemblies) rules.Add(ExclusionRules.TestAssemblies);
            if (_hideGenerated) rules.Add(ExclusionRules.GeneratedCode);
            if (_hideNetwork) rules.Add(ExclusionRules.Network);
            if (_hideWpfUi) rules.Add(ExclusionRules.WpfUi);

            // The "before" baseline drops the same assemblies but none of the type
            // filters, so before/after compares like with like: same scope, more filters.
            _unfilteredInScope = TreeFilter.Scope(_report.Root,
                _hideTestAssemblies ? new[] { ExclusionRules.TestAssemblies } : Array.Empty<ExclusionRule>())
                ?? _report.Root;
            _unfilteredInScope.Aggregate();

            _scoped = TreeFilter.Scope(_report.Root, rules, RecordExclusion)
                      ?? _report.Root.CloneShallow(pinTotals: false);
            _scoped.Aggregate();

            // Pass 2: hide rows without touching the numbers.

            var settings = CurrentFilters;
            foreach (var module in _scoped.Children)
            {
                var kept = TreeFilter.Filter(module, settings);
                if (kept is not null)
                    Tree.Add(kept);
            }

            foreach (var node in ScopedClasses
                         .Where(c => c.HasData && c.LinePercent < _threshold)
                         .OrderBy(c => c.LinePercent)
                         .ThenByDescending(c => c.LinesNotCovered)
                         .Take(50))
            {
                Hotspots.Add(node);
            }
        }

        RaiseAll();
    }

    /// <summary>
    /// Removes everything an active rule matches. Parents that lose a child stop being
    /// self-measured so their totals are recomputed from what survived — otherwise the
    /// headline percentage would still be counting code that's no longer on screen.
    /// </summary>
    private FilterSettings CurrentFilters => new()
    {
        Search = _search,
        OnlyBelowThreshold = _onlyBelowThreshold,
        HideMethods = _hideMethods,
        Threshold = _threshold
    };

    private void RecordExclusion(CoverageNode node, ExclusionRule rule)
    {
        if (node.Kind == NodeKind.Module)
            _excludedModules.Add(node.Name);

        var types = node.Kind == NodeKind.Class ? 1 : 0;
        types += node.Descendants().Count(n => n.Kind == NodeKind.Class);

        var current = _excluded.TryGetValue(rule.Name, out var existing) ? existing : (0, 0);
        _excluded[rule.Name] = (current.Item1 + types, current.Item2 + node.CoverableLines);
    }

    /// <summary>
    /// Keeps a node when it matches the filters itself, or when any descendant survives.
    /// Coverage filtering is applied to types and methods only, so a healthy-looking
    /// assembly still shows up when it hides an untested class.
    /// </summary>
}
