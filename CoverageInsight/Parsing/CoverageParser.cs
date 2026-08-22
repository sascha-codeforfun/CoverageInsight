using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoverageInsight.Filtering;
using CoverageInsight.Models;

namespace CoverageInsight.Parsing;

/// <summary>
/// Reads the three XML shapes a .NET team actually ends up with:
///   1. results         — the current Microsoft XML format, written by VS 2022/2026 and
///                        Microsoft.CodeCoverage 17.x via `--collect "Code Coverage;Format=xml"`.
///   2. CoverageDSPriv  — the legacy shape from `CodeCoverage.exe analyze` and older
///                        `dotnet-coverage merge -f xml`.
///   3. Cobertura       — what coverlet (`--collect:"XPlat Code Coverage"`) produces.
/// </summary>
public static class CoverageParser
{
    private static readonly Regex ConditionCoverage =
        new(@"\((?<hit>\d+)\s*/\s*(?<total>\d+)\)", RegexOptions.Compiled);

    public static CoverageReport Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("That file is gone or the path is wrong.", path);

        GuardAgainstBinaryCoverage(path);

        XDocument doc;
        try
        {
            doc = XDocument.Load(path, LoadOptions.None);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't well-formed XML: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new InvalidDataException("The document has no root element.");

        return root.Name.LocalName.ToLowerInvariant() switch
        {
            "results" => ParseMicrosoftXml(root, path),
            "coveragedspriv" => ParseVisualStudio(root, path),
            "coverage" => ParseCobertura(root, path),
            _ => throw new NotSupportedException(BuildUnknownFormatMessage(root))
        };
    }

    /// <summary>The binary .coverage file isn't XML; say so with the conversion command.</summary>
    private static void GuardAgainstBinaryCoverage(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        var read = stream.Read(head);

        for (var i = 0; i < read; i++)
        {
            var b = head[i];
            if (b is 0xEF or 0xBB or 0xBF || char.IsWhiteSpace((char)b)) continue; // BOM or leading space
            if (b == (byte)'<') return;                                            // looks like XML

            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is a binary coverage file, not XML. Convert it first:\n\n" +
                "  dotnet-coverage merge -o report.xml -f xml \"" + Path.GetFileName(path) + "\"");
        }
    }

    private static string BuildUnknownFormatMessage(XElement root)
    {
        var children = string.Join(", ", root.Elements().Take(4).Select(e => "<" + e.Name.LocalName + ">"));
        return $"Root element <{root.Name.LocalName}> isn't a format this app reads" +
               (children.Length > 0 ? $" (its first children are {children})" : string.Empty) + ".\n\n" +
               "Supported roots: <results> (Microsoft XML, VS 2022/2026), " +
               "<CoverageDSPriv> (legacy CodeCoverage.exe), <coverage> (Cobertura).";
    }

    // ------------------------------------------------------------------
    // Microsoft XML — <results><modules><module><functions><function>
    // ------------------------------------------------------------------

    private static CoverageReport ParseMicrosoftXml(XElement root, string path)
    {
        var reportRoot = new CoverageNode { Kind = NodeKind.Report, Name = Path.GetFileName(path) };
        var report = new CoverageReport
        {
            SourcePath = path,
            FormatName = "Microsoft XML (VS 2022/2026, Microsoft.CodeCoverage 17.x)",
            Root = reportRoot
        };

        var modules = Kids(root, "modules").SelectMany(m => Kids(m, "module"));

        foreach (var moduleEl in modules)
        {
            var module = new CoverageNode
            {
                Kind = NodeKind.Module,
                Name = FirstNonEmpty(Attr(moduleEl, "name"), Attr(moduleEl, "path"), "(unnamed module)")
            };
            reportRoot.Add(module);

            // source_id -> path, scoped to this module
            var sources = new Dictionary<int, string>();
            foreach (var sourceEl in Kids(moduleEl, "source_files").SelectMany(s => Kids(s, "source_file")))
            {
                var sourcePath = Attr(sourceEl, "path");
                if (!string.IsNullOrWhiteSpace(sourcePath))
                    sources[IntAttr(sourceEl, "id")] = sourcePath;
            }

            var namespaces = new Dictionary<string, CoverageNode>(StringComparer.Ordinal);
            var types = new Dictionary<string, CoverageNode>(StringComparer.Ordinal);

            foreach (var functionEl in Kids(moduleEl, "functions").SelectMany(f => Kids(f, "function")))
            {
                var nsName = FirstNonEmpty(Attr(functionEl, "namespace"), "(global namespace)");
                var rawTypeName = FirstNonEmpty(Attr(functionEl, "type_name"), "(unnamed type)");
                var typeName = MemberNames.NormalizeType(rawTypeName);

                if (!namespaces.TryGetValue(nsName, out var ns))
                {
                    ns = new CoverageNode { Kind = NodeKind.Namespace, Name = nsName };
                    namespaces[nsName] = ns;
                    module.Add(ns);
                }

                var typeKey = nsName + "\u0000" + typeName;
                if (!types.TryGetValue(typeKey, out var type))
                {
                    type = new CoverageNode { Kind = NodeKind.Class, Name = typeName };
                    types[typeKey] = type;
                    ns.Add(type);
                }

                var method = new CoverageNode
                {
                    Kind = NodeKind.Method,
                    Name = FirstNonEmpty(Attr(functionEl, "name"), "(unnamed function)"),
                    DeclaringType = rawTypeName
                };

                method.LinesCovered = IntAttr(functionEl, "lines_covered");
                method.LinesPartiallyCovered = IntAttr(functionEl, "lines_partially_covered");
                method.LinesNotCovered = IntAttr(functionEl, "lines_not_covered");
                method.BlocksCovered = IntAttr(functionEl, "blocks_covered");
                method.BlocksNotCovered = IntAttr(functionEl, "blocks_not_covered");

                ReadRanges(functionEl, method, sources, countLines: !method.HasData);
                type.SourceFile ??= method.SourceFile;
                type.FirstLine ??= method.FirstLine;

                method.SelfMeasured = true;
                type.Add(method);
            }

            // Modules with no instrumented functions still carry their own totals.
            if (module.Children.Count == 0)
            {
                module.SelfMeasured = true;
                module.LinesCovered = IntAttr(moduleEl, "lines_covered");
                module.LinesPartiallyCovered = IntAttr(moduleEl, "lines_partially_covered");
                module.LinesNotCovered = IntAttr(moduleEl, "lines_not_covered");
                module.BlocksCovered = IntAttr(moduleEl, "blocks_covered");
                module.BlocksNotCovered = IntAttr(moduleEl, "blocks_not_covered");
            }
        }

        AddSkipNotes(root, report);

        if (reportRoot.Children.Count == 0)
            report.Notes.Add("No <module> elements were instrumented — check that .pdb files were next to the assemblies.");

        reportRoot.Aggregate();
        reportRoot.SortRecursive();
        return report;
    }

    /// <summary>
    /// Ranges give us the source file and first line. When a function carries no line
    /// attributes (some emitters omit them) the ranges are counted instead:
    /// covered="yes" | "no" | "partial".
    /// </summary>
    private static void ReadRanges(XElement functionEl, CoverageNode method,
                                   IReadOnlyDictionary<int, string> sources, bool countLines)
    {
        int? first = null;

        foreach (var rangeEl in Kids(functionEl, "ranges").SelectMany(r => Kids(r, "range")))
        {
            var start = IntAttr(rangeEl, "start_line");
            if (start > 0 && (first is null || start < first))
                first = start;

            if (method.SourceFile is null && sources.TryGetValue(IntAttr(rangeEl, "source_id"), out var file))
                method.SourceFile = file;

            var covered = Attr(rangeEl, "covered").ToLowerInvariant();
            if (covered is not ("yes" or "partial"))
            {
                var end = IntAttr(rangeEl, "end_line");
                method.AddMissedLines(start, end > 0 ? end : start);
            }

            if (!countLines) continue;

            switch (covered)
            {
                case "yes": method.LinesCovered++; break;
                case "partial": method.LinesPartiallyCovered++; break;
                default: method.LinesNotCovered++; break;
            }
        }

        method.FirstLine = first;
    }

    /// <summary>Skipped modules and functions are the most common reason a report looks wrong.</summary>
    private static void AddSkipNotes(XElement root, CoverageReport report)
    {
        var skippedModules = Kids(root, "skipped_modules").SelectMany(s => Kids(s, "skipped_module")).ToList();
        if (skippedModules.Count > 0)
        {
            var byReason = skippedModules
                .GroupBy(m => FirstNonEmpty(Attr(m, "reason"), "unspecified"))
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");
            report.Notes.Add($"{skippedModules.Count} modules skipped ({string.Join(", ", byReason)}).");
        }

        var skippedFunctions = Kids(root, "modules")
            .SelectMany(m => Kids(m, "module"))
            .SelectMany(m => Kids(m, "skipped_functions"))
            .SelectMany(s => Kids(s, "skipped_function"))
            .Count();

        if (skippedFunctions > 0)
            report.Notes.Add($"{skippedFunctions} functions excluded from instrumentation.");
    }

    // ------------------------------------------------------------------
    // Visual Studio
    // ------------------------------------------------------------------

    private static CoverageReport ParseVisualStudio(XElement root, string path)
    {
        var sourceFiles = new Dictionary<int, string>();
        foreach (var sf in Kids(root, "SourceFileNames"))
        {
            var id = Int(sf, "SourceFileID");
            var name = Text(sf, "SourceFileName");
            if (id > 0 && !string.IsNullOrWhiteSpace(name))
                sourceFiles[id] = name;
        }

        var reportRoot = new CoverageNode { Kind = NodeKind.Report, Name = Path.GetFileName(path) };
        var report = new CoverageReport
        {
            SourcePath = path,
            FormatName = "Visual Studio (CoverageDSPriv)",
            Root = reportRoot
        };

        foreach (var moduleEl in Kids(root, "Module"))
        {
            var module = new CoverageNode
            {
                Kind = NodeKind.Module,
                Name = FirstNonEmpty(Text(moduleEl, "ModuleName"), "(unnamed module)")
            };
            ReadVsCounters(moduleEl, module);
            reportRoot.Add(module);

            foreach (var nsEl in Kids(moduleEl, "NamespaceTable"))
            {
                var ns = new CoverageNode
                {
                    Kind = NodeKind.Namespace,
                    Name = FirstNonEmpty(Text(nsEl, "NamespaceName"), "(global namespace)")
                };
                ReadVsCounters(nsEl, ns);
                module.Add(ns);

                foreach (var classEl in Kids(nsEl, "Class"))
                {
                    var rawClassName = FirstNonEmpty(Text(classEl, "ClassName"), "(unnamed type)");
                    var cls = new CoverageNode
                    {
                        Kind = NodeKind.Class,
                        Name = MemberNames.NormalizeType(rawClassName)
                    };
                    ReadVsCounters(classEl, cls);
                    ns.Add(cls);

                    foreach (var methodEl in Kids(classEl, "Method"))
                    {
                        var method = new CoverageNode
                        {
                            Kind = NodeKind.Method,
                            Name = FirstNonEmpty(
                                Text(methodEl, "MethodFullName"),
                                Text(methodEl, "MethodName"),
                                "(unnamed method)"),
                            DeclaringType = rawClassName
                        };
                        ReadVsCounters(methodEl, method);
                        ReadVsMethodLines(methodEl, method, sourceFiles);
                        cls.Add(method);
                    }

                    // A type with no listed methods keeps its own numbers.
                    if (cls.Children.Count == 0)
                        cls.SelfMeasured = true;

                    cls.SourceFile ??= cls.Children.Select(c => c.SourceFile).FirstOrDefault(f => f is not null);
                }
            }
        }

        if (reportRoot.Children.Count == 0)
            report.Notes.Add("The file parsed, but it contains no modules — the test run probably instrumented nothing.");

        reportRoot.Aggregate();
        reportRoot.SortRecursive();
        return report;
    }

    private static void ReadVsCounters(XElement el, CoverageNode node)
    {
        node.LinesCovered = Int(el, "LinesCovered");
        node.LinesPartiallyCovered = Int(el, "LinesPartiallyCovered");
        node.LinesNotCovered = Int(el, "LinesNotCovered");
        node.BlocksCovered = Int(el, "BlocksCovered");
        node.BlocksNotCovered = Int(el, "BlocksNotCovered");
    }

    private static void ReadVsMethodLines(XElement methodEl, CoverageNode method, IReadOnlyDictionary<int, string> files)
    {
        int? first = null;
        foreach (var lineEl in Kids(methodEl, "Lines"))
        {
            var start = Int(lineEl, "LnStart");
            if (start > 0 && (first is null || start < first))
                first = start;

            if (method.SourceFile is null)
            {
                var id = Int(lineEl, "SourceFileID");
                if (files.TryGetValue(id, out var file))
                    method.SourceFile = file;
            }

            // Coverage: 0 = covered, 1 = partially covered, 2 = not covered
            if (Int(lineEl, "Coverage") == 2)
            {
                var end = Int(lineEl, "LnEnd");
                method.AddMissedLines(start, end > 0 ? end : start);
            }
        }
        method.FirstLine = first;
    }

    // ------------------------------------------------------------------
    // Cobertura
    // ------------------------------------------------------------------

    private static CoverageReport ParseCobertura(XElement root, string path)
    {
        var reportRoot = new CoverageNode { Kind = NodeKind.Report, Name = Path.GetFileName(path) };
        var report = new CoverageReport
        {
            SourcePath = path,
            FormatName = "Cobertura (coverlet / ReportGenerator)",
            Root = reportRoot
        };

        var packages = root.Elements()
            .Where(e => Is(e, "packages"))
            .SelectMany(e => e.Elements().Where(p => Is(p, "package")))
            .ToList();

        foreach (var packageEl in packages)
        {
            var module = new CoverageNode
            {
                Kind = NodeKind.Module,
                Name = FirstNonEmpty(Attr(packageEl, "name"), "(unnamed package)")
            };
            reportRoot.Add(module);

            var namespaces = new Dictionary<string, CoverageNode>(StringComparer.Ordinal);

            var classes = packageEl.Elements().Where(e => Is(e, "classes"))
                                   .SelectMany(e => e.Elements().Where(c => Is(c, "class")));

            foreach (var classEl in classes)
            {
                var rawName = FirstNonEmpty(Attr(classEl, "name"), "(unnamed type)");
                SplitTypeName(rawName, out var nsName, out var typeName);
                var rawTypeName = typeName;
                typeName = MemberNames.NormalizeType(typeName);

                if (!namespaces.TryGetValue(nsName, out var ns))
                {
                    ns = new CoverageNode { Kind = NodeKind.Namespace, Name = nsName };
                    namespaces[nsName] = ns;
                    module.Add(ns);
                }

                var cls = new CoverageNode
                {
                    Kind = NodeKind.Class,
                    Name = typeName,
                    SourceFile = Attr(classEl, "filename"),
                    SelfMeasured = true // class-level <lines> is the authoritative total
                };
                ns.Add(cls);

                var classLines = classEl.Elements().FirstOrDefault(e => Is(e, "lines"));
                ApplyCoberturaLines(classLines, cls);

                var methods = classEl.Elements().Where(e => Is(e, "methods"))
                                     .SelectMany(e => e.Elements().Where(m => Is(m, "method")));

                foreach (var methodEl in methods)
                {
                    var method = new CoverageNode
                    {
                        Kind = NodeKind.Method,
                        Name = Attr(methodEl, "name") + Attr(methodEl, "signature"),
                        DeclaringType = rawTypeName,
                        SourceFile = cls.SourceFile
                    };
                    ApplyCoberturaLines(methodEl.Elements().FirstOrDefault(e => Is(e, "lines")), method);
                    cls.Add(method);
                }

                // If the class had no <lines> of its own, fall back to the methods.
                if (!cls.HasData && cls.Children.Count > 0)
                    cls.SelfMeasured = false;
            }
        }

        if (reportRoot.Children.Count == 0)
            report.Notes.Add("No <package> elements found — the Cobertura file appears to be empty.");

        reportRoot.Aggregate();
        reportRoot.SortRecursive();
        return report;
    }

    private static void ApplyCoberturaLines(XElement? linesEl, CoverageNode node)
    {
        if (linesEl is null) return;

        int? first = null;

        foreach (var lineEl in linesEl.Elements().Where(e => Is(e, "line")))
        {
            var number = IntAttr(lineEl, "number");
            if (number > 0 && (first is null || number < first))
                first = number;

            var hits = IntAttr(lineEl, "hits");
            var isBranch = string.Equals(Attr(lineEl, "branch"), "true", StringComparison.OrdinalIgnoreCase);

            var branchHit = 0;
            var branchTotal = 0;
            if (isBranch)
            {
                var match = ConditionCoverage.Match(Attr(lineEl, "condition-coverage"));
                if (match.Success)
                {
                    branchHit = int.Parse(match.Groups["hit"].Value, CultureInfo.InvariantCulture);
                    branchTotal = int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);
                }
                node.BranchesCovered += branchHit;
                node.BranchesTotal += branchTotal;
            }

            if (hits <= 0)
            {
                node.LinesNotCovered++;
                node.AddMissedLines(number, number);
            }
            else if (isBranch && branchTotal > 0 && branchHit < branchTotal)
                node.LinesPartiallyCovered++;
            else
                node.LinesCovered++;
        }

        node.FirstLine ??= first;
    }

    private static void SplitTypeName(string raw, out string ns, out string typeName)
    {
        // Ignore dots inside generic arguments: Foo.Bar<System.Int32>
        var depth = 0;
        var split = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '<' || c == '[') depth++;
            else if (c == '>' || c == ']') depth--;
            else if (c == '.' && depth == 0) split = i;
        }

        if (split < 0)
        {
            ns = "(global namespace)";
            typeName = raw;
        }
        else
        {
            ns = raw[..split];
            typeName = raw[(split + 1)..];
        }
    }

    // ------------------------------------------------------------------
    // XML helpers (case-insensitive, namespace-agnostic)
    // ------------------------------------------------------------------

    private static bool Is(XElement el, string localName)
        => string.Equals(el.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<XElement> Kids(XElement parent, string localName)
        => parent.Elements().Where(e => Is(e, localName));

    private static string Text(XElement parent, string localName)
        => Kids(parent, localName).FirstOrDefault()?.Value.Trim() ?? string.Empty;

    private static string Attr(XElement el, string name)
        => el.Attributes().FirstOrDefault(a =>
               string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static int Int(XElement parent, string localName)
        => int.TryParse(Text(parent, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int IntAttr(XElement el, string name)
        => int.TryParse(Attr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;
}
