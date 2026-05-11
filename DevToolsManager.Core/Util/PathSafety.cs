using System.Text.RegularExpressions;

namespace DevToolsManager.Core.Util;

public static partial class PathSafety
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+.][A-Za-z0-9._-]+)?$", RegexOptions.Compiled)]
    private static partial Regex SdkVersionRegex();

    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+)?(?:[-+.][A-Za-z0-9._-]+)?$", RegexOptions.Compiled)]
    private static partial Regex ChannelVersionRegex();

    // JetBrains stable version: 2-4 numeric parts (2026.1, 2026.1.1, 2026.1.1.1).
    // Deliberately does not allow suffixes; EAP / preview versions are skipped.
    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+){0,2}$", RegexOptions.Compiled)]
    private static partial Regex IdeVersionRegex();

    public static bool IsValidSdkVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && SdkVersionRegex().IsMatch(version);

    public static bool IsValidChannelVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && ChannelVersionRegex().IsMatch(version);

    public static bool IsValidIdeVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && IdeVersionRegex().IsMatch(version);

    public static string RequireValidSdkVersion(string version, string paramName)
    {
        if (!IsValidSdkVersion(version))
        {
            throw new ArgumentException($"Invalid SDK version: '{version}'.", paramName);
        }
        return version;
    }

    public static string RequireValidChannelVersion(string version, string paramName)
    {
        if (!IsValidChannelVersion(version))
        {
            throw new ArgumentException($"Invalid channel version: '{version}'.", paramName);
        }
        return version;
    }

    public static string RequireValidIdeVersion(string version, string paramName)
    {
        if (!IsValidIdeVersion(version))
        {
            throw new ArgumentException($"Invalid IDE version: '{version}'.", paramName);
        }
        return version;
    }

    public static string RequireValidFileName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("File name must not be empty.", paramName);
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"File name contains invalid characters: '{name}'.", paramName);
        }

        if (name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            name == "." || name == "..")
        {
            throw new ArgumentException($"File name must not be a path: '{name}'.", paramName);
        }

        return name;
    }

    public static string CombineSafe(string root, string childName)
    {
        RequireValidFileName(childName, nameof(childName));

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, childName));

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) &&
            !string.Equals(fullPath, fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resolved path '{fullPath}' escapes the intended root '{fullRoot}'.");
        }

        return fullPath;
    }

    public static bool IsInsideRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(rootWithSeparator, StringComparison.Ordinal) ||
               string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal);
    }
}
