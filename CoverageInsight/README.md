# Coverage Insight

A small WPF app that reads a .NET coverage report and turns it into something you can
actually act on: rolled-up totals, a drillable tree, a "what do I test next" queue, and
a standalone HTML report you can attach to a build or email to someone.

No NuGet packages, so it restores and builds offline.

## Build and run

```powershell
dotnet run --project CoverageInsight
# or: dotnet run --project CoverageInsight -- C:\path\to\report.coveragexml
dotnet test          # unit tests
```

Requires the .NET 10 SDK on Windows. `dotnet publish -c Release -r win-x64 --self-contained false`
gives you a single folder you can drop on a build agent.

Open a file with the button, drag one onto the window, or pass it as the first argument.
`sample-report-vs2026.xml` in this folder is there so you can see the UI populated straight away.

## Input formats

| Format | Root element | Where it comes from |
| --- | --- | --- |
| Microsoft XML | `<results>` | VS 2022/2026 and Microsoft.CodeCoverage 17.x — `dotnet test --collect "Code Coverage;Format=xml"` |
| Visual Studio (legacy) | `<CoverageDSPriv>` | `CodeCoverage.exe analyze`, older `dotnet-coverage merge -f xml` |
| Cobertura | `<coverage>` | coverlet, i.e. `dotnet test --collect:"XPlat Code Coverage"` |

The `<results>` shape is the current one: modules hold flat `<function>` elements carrying
`namespace` and `type_name`, which the app regroups into the namespace → type → method tree.
Its `<skipped_modules>` and `<skipped_functions>` sections are surfaced as notes, since "why is
this assembly missing" is nearly always answered by `reason="no_symbols"`.

The binary `.coverage` file isn't XML. Dropping one in gives you the conversion command
rather than a parse error:

```powershell
dotnet tool install --global dotnet-coverage
dotnet-coverage merge -o report.xml -f xml TestResults\**\*.coverage
```

Both `sample-report.coveragexml` (legacy) and `sample-report-vs2026.xml` (current) are in this
folder so you can check either parser without running a build.

All parsers match element and attribute names case-insensitively and ignore XML namespaces,
so minor tooling differences don't break them.

## Narrowing to what's worth testing

Two toggles under **HIDE** drop whole categories of code that rarely repays a unit test:

- **Network code** — HTTP and socket clients, transports, gateways, generated service references.
- **WPF UI code** — windows, views, code-behind, and XAML-generated partials. ViewModels are
  deliberately kept: in a WPF app they're usually the most valuable thing to test. Value
  converters are kept too, for the same reason.

These don't just hide rows. Excluded code leaves the totals entirely, so the headline
percentage becomes the coverage of the code you actually intend to test, and the label to the
right of the toggles says exactly what was removed. The HTML and CSV exports follow the same
scope and record the exclusion in their notes.

The patterns live in `Filtering/ExclusionRules.cs`, one regex per line with comments, and each
checkbox's tooltip lists the patterns currently in force. Tune them for your codebase — the
defaults are careful about near-misses (`Review`, `Overview` and `PreviewBuilder` are not
treated as views), but only you know what your namespaces mean. Adding a third category —
generated code, EF migrations, DTOs — means adding one `ExclusionRule` and one checkbox.

## What you get beyond the raw XML

- **Rolled-up totals.** Lines, blocks and branches aggregated from the leaves up, so a
  namespace total is always consistent with the methods under it.
- **Partial lines kept separate.** A line where only one branch ran is neither covered nor
  missed. It gets its own colour and its own count instead of being rounded into the
  headline percentage.
- **A work queue.** Every type under your target percentage, worst first, with the number
  of lines that were never hit — that ordering is the point, since a type at 0% with 200
  missed lines matters more than one at 60% with 4.
- **Filters that survive the hierarchy.** Search matches type, method and file names;
  "only what's under target" hides healthy leaves but keeps the assemblies and namespaces
  that contain unhealthy ones, so nothing disappears silently.
- **HTML export.** One self-contained file, no CDN references, collapsible tree, prints cleanly.
- **CSV export.** One row per node with all counters, for a spreadsheet or a build gate.
- **CSV for AI.** A different shape for a different reader: one row per method below 100%,
  sorted by lines missed, with the line numbers that never ran collapsed into ranges
  (`118-121;129-139`). Compiler-generated names such as `<One>b__9_14` are kept verbatim
  rather than folded into their parent — a lambda that never ran is a distinct, actionable
  fact. Branch data, file paths and per-line hit counts are omitted; they multiply the size
  without changing what gets written next. Two header lines record which filters were applied
  and the totals before and after, so a missing method can be read as "fully covered" rather
  than "a filter ate it". The `Types only` toggle is ignored for this export, since a
  method-level file with no methods would be worse than useless.

  Rows are ranked by `lines_missed × role weight` and sorted, so the first screenful is the
  queue. Weighting only ever demotes: presentation ×0.4 and io ×0.7, because UI failures are
  visible by definition and malformed input usually throws, while parsers and derived-value
  code return a plausible wrong answer with nothing thrown and nothing logged. Anything
  unmatched keeps full weight, so an unrecognised type is never quietly pushed down.
  Patterns live in `Filtering/RoleRules.cs`.

  **Folding** (on by default, toggleable) maps async bodies, iterators, lambdas and local
  functions back to the method that declared them. Without it a method whose body is mostly
  `await` or LINQ has its uncovered lines split across a dozen generated members, none large
  enough to rank — the large untested method hides. The `generated` column records what was
  folded, and the missed ranges are merged rather than lost. Folding drops parameter lists,
  since an IL name carries only the declaring method's name; overloads therefore merge into
  one row.
- **Test assemblies are out of scope by default.** A test project's own coverage is near-total
  by construction, so summing it into the headline pulls the number toward the tests rather
  than the code under test. The `Test assemblies` toggle drops them, and the CSV `# scope:`
  line names what was kept and what was dropped. Detection is by assembly name only.
- **Copy digest.** A markdown summary sized for pasting into a chat, with an estimated token
  count reported after it's copied.

## Keyboard

| Key | Action |
| --- | --- |
| Ctrl+O | Open report |
| Ctrl+F | Focus the filter box |
| Ctrl+E | Save HTML report |
| F5 | Reload the current file |
| Esc | Clear the filter |

## Layout

```
CoverageInsight.csproj
App.xaml / App.xaml.cs        entry point, accepts a file path argument
Assets/                       app.ico (exe icon), app_256.png (window icon), app.svg, app_1024.png
Theme.xaml                    palette, typography, control styles
MainWindow.xaml(.cs)          shell: header, KPIs, filters, tree, work queue
Converters.cs                 ribbon sizing and percentage colouring
Models/CoverageNode.cs        one node type for every level, plus rollup and filtering
Models/CoverageReport.cs      the loaded file
Parsing/CoverageParser.cs     all three XML formats
Filtering/ExclusionRules.cs   the network / WPF UI pattern sets — tune these
Reporting/HtmlReportWriter.cs standalone HTML
Reporting/AiCsvWriter.cs      method-level CSV with missed line ranges
Reporting/ContextDigestWriter.cs  compact markdown digest
Reporting/ReportIcon.cs       the mark as an embedded data URI for exports
Filtering/TreeFilter.cs       scoping and display filtering, kept out of the view model
Filtering/MemberNames.cs      compiler-generated member folding
../CoverageInsight.Tests/     unit tests for the parsing, filtering and export logic
Reporting/CsvReportWriter.cs  flat CSV
ViewModels/MainViewModel.cs   load, filter, hotspots, export
ViewModels/Mvvm.cs            tiny INotifyPropertyChanged + ICommand helpers
```

## Notes on the numbers

- **Line percentage** is `covered / (covered + partial + missed)`. Partially covered lines
  count against you; the ribbon shows them in amber so you can see how much of the gap
  they represent.
- **Blocks** only appear in Visual Studio reports; **branches** only in Cobertura. The KPI
  reads `n/a` for whichever the file doesn't carry rather than showing a fake zero.
- Cobertura reports coverage per type at the type level, and that total can include
  members that never appear under a method (field initialisers, for example). Those totals
  are treated as authoritative rather than being recomputed from the method list.
