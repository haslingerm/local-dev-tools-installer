namespace DevToolsManager.Core.Install;

/// <summary>
/// Smoke test for IDE installs: verifies <c>build.txt</c> at the IDE root matches
/// the build number from the catalog. No process execution — avoids the GUI
/// side-effects of <c>--version</c> on systems without DISPLAY/XAUTHORITY.
/// </summary>
public sealed class IdeSmokeTest
{
    public async ValueTask<(bool ok, string output)> TestInstallAsync(
        string ideRoot,
        string expectedBuild,
        CancellationToken ct = default)
    {
        var path = Path.Combine(ideRoot, "build.txt");
        if (!File.Exists(path))
        {
            return (false, $"build.txt not found at {path}");
        }

        var actual = (await File.ReadAllTextAsync(path, ct)).Trim();
        if (string.IsNullOrEmpty(actual))
        {
            return (false, $"build.txt at {path} is empty.");
        }

        // The catalog's `build` field omits the product prefix (e.g. "261.23567.144"),
        // while build.txt in the archive includes it ("RD-261.23567.144").
        // Substring match handles both cases without needing per-product knowledge.
        if (!actual.Contains(expectedBuild, StringComparison.OrdinalIgnoreCase))
        {
            return (false,
                $"build.txt mismatch. Expected to contain '{expectedBuild}', got '{actual}'.");
        }
        return (true, actual);
    }
}
