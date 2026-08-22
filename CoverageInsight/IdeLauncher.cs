using System;
using System.Diagnostics;
using System.IO;

namespace CoverageInsight;

/// <summary>
/// Opens a source file where you can act on it.
///
/// Visual Studio's <c>/Edit</c> switch hands the file to an instance that is already
/// running, which is what makes this useful: the file opens in the solution you are
/// working in rather than in a fresh window.
///
/// Jumping to a line is deliberately not attempted. <c>/Command "Edit.GoTo n"</c> works
/// on its own but is ignored when combined with <c>/Edit</c>, and used alone it spawns a
/// second instance — worse than landing at line 1 in the right one. Doing it properly
/// means driving the running IDE over COM, which is a larger and more fragile thing than
/// this feature is worth today.
/// </summary>
public static class IdeLauncher
{
    /// <summary>Asks the VS installer where the newest install lives. Version-proof, unlike a hardcoded path.</summary>
    private static string? FindVisualStudio()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (!File.Exists(vswhere))
            return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(vswhere)
            {
                Arguments = "-latest -prerelease -property productPath",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return null;

            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the file in Visual Studio, falling back to selecting it in Explorer.
    /// Returns a line describing what happened, for the status bar.
    /// </summary>
    public static string Open(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            return "That row carries no source file.";

        if (!File.Exists(sourceFile))
            return "That source path isn't on this machine: " + sourceFile;

        var devenv = FindVisualStudio();

        try
        {
            if (devenv is not null)
            {
                Process.Start(new ProcessStartInfo(devenv)
                {
                    Arguments = $"/Edit \"{sourceFile}\"",
                    UseShellExecute = true
                });
                return "Opened in Visual Studio: " + Path.GetFileName(sourceFile);
            }

            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{sourceFile}\"",
                UseShellExecute = true
            });
            return "Visual Studio not found; showed the file in Explorer instead.";
        }
        catch (Exception ex)
        {
            return "Couldn't open the file: " + ex.Message;
        }
    }
}
