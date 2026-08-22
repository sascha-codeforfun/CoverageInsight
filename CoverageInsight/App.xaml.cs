using System.Windows;

namespace CoverageInsight;

public partial class App : Application
{
    /// <summary>Supports "CoverageInsight.exe report.coveragexml" from a build script.</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0)
            StartupFile = e.Args[0];

        base.OnStartup(e);
    }
}
