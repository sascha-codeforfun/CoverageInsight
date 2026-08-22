namespace CoverageInsight.Reporting;

/// <summary>
/// The app mark as a 48px PNG data URI, so an exported report keeps its identity
/// without pulling anything off the network. Regenerate from Assets/app_1024.png
/// if the icon ever changes.
/// </summary>
internal static class ReportIcon
{
    public const string DataUri =
        "data:image/png;base64," +
        "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" +
        "CC";
}
