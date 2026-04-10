#nullable enable

using System;
using System.IO;

namespace ExplorerHelper.Shared;


/// <summary>
/// Shared utility methods used across commands.
/// </summary>
public static class Utilities
{
    /// <summary>
    /// Escapes a string for use in an XML attribute value.
    /// </summary>
    public static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }


    /// <summary>
    /// Resolves a snapshot path from the user's argument.
    ///   null         → %TEMP%/prefix_datetime/filename.xml
    ///   folder path  → folder/filename_datetime.xml
    ///   file.xml     → exact path
    /// </summary>
    public static string ResolveSnapshotPath(string? pathArg, string prefix, string filename)
    {

        if (string.IsNullOrEmpty(pathArg)) {
            string aTempDir = Path.Combine(
                Path.GetTempPath(),
                $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}");
            return Path.Combine(aTempDir, $"{filename}.xml");
        }

        if (pathArg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(pathArg);

        return Path.GetFullPath(
            Path.Combine(pathArg, $"{filename}_{DateTime.Now:yyyyMMdd_HHmmss}.xml"));

    }
}
