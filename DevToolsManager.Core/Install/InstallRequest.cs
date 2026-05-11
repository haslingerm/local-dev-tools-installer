using System.Security.Cryptography;
using DevToolsManager.Core.Models;

namespace DevToolsManager.Core.Install;

/// <summary>
/// A product-agnostic install request consumed by <see cref="ProductInstaller"/>.
/// Both .NET SDK and JetBrains IDE flows build one of these and hand it off.
/// </summary>
/// <param name="DownloadUrl">URL of the archive. Ignored when SideloadPath is set.</param>
/// <param name="FileName">
/// Bare archive filename used as the cache key. Validated as a safe filename.
/// </param>
/// <param name="ExpectedHash">Hex digest expected for the archive. Empty disables hash check.</param>
/// <param name="HashAlgorithm">Algorithm matching ExpectedHash (SHA-512 for .NET, SHA-256 for JetBrains).</param>
/// <param name="ExpectedSize">Expected size in bytes; 0 disables size enforcement.</param>
/// <param name="IsHashVerified">
/// True when the hash came from a trusted catalog (will hard-fail on mismatch).
/// False for sideloaded archives we couldn't match against any catalog entry.
/// </param>
/// <param name="SideloadPath">If set, use this local archive instead of downloading.</param>
/// <param name="TargetInstallDir">
/// Final install directory. Must be a fully-resolved path; its parent will host the staging dir.
/// </param>
/// <param name="SmokeTest">
/// Run after extraction (against the staging dir) to validate the install before swap.
/// Returns (ok, output) where output is shown to the user on failure.
/// </param>
/// <param name="ExtractLimits">
/// Bounded-extract limits. Pass null to skip bounds (only safe when IsHashVerified is true).
/// </param>
/// <param name="CompletionMessage">
/// Phrase shown in the Done progress event ("SDK 10.0.105 installed successfully.", etc.).
/// </param>
public sealed record InstallRequest(
    string DownloadUrl,
    string FileName,
    string ExpectedHash,
    HashAlgorithmName HashAlgorithm,
    long ExpectedSize,
    bool IsHashVerified,
    string? SideloadPath,
    string TargetInstallDir,
    Func<string, CancellationToken, ValueTask<(bool ok, string output)>> SmokeTest,
    ArchiveExtractionLimits? ExtractLimits = null,
    string CompletionMessage = "Installed successfully.");
