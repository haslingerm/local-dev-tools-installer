using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DotnetSdkManager.Core.Process;
using DotnetSdkManager.Core.Util;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DotnetSdkManager.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformIntegration : IPlatformIntegration
{
    private readonly IProcessRunner _runner;
    private readonly string _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public WindowsPlatformIntegration(IProcessRunner runner) => _runner = runner;

    public string DataDir => Path.Combine(_localAppData, "DotnetSdkManager");
    public string InstallRoot => Path.Combine(DataDir, "sdks");
    public string ActiveLinkPath => Path.Combine(DataDir, "active");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string SideloadDir => Path.Combine(DataDir, "sideload");
    public string ArchiveExtension => ".zip";

    public string CurrentRid =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public IEnumerable<string> SystemSdkScanPaths
    {
        get
        {
            yield return @"C:\Program Files\dotnet\sdk";
            yield return @"C:\Program Files (x86)\dotnet\sdk";
            yield return Path.Combine(_localAppData, "Microsoft", "dotnet", "sdk");
        }
    }

    public ValueTask WriteEnvironmentAsync(string activeLinkPath, CancellationToken ct = default)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? Registry.CurrentUser.CreateSubKey("Environment", writable: true);

        var currentPath = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";

        if (!currentPath.Contains(activeLinkPath, StringComparison.OrdinalIgnoreCase))
        {
            var newPath = string.IsNullOrEmpty(currentPath)
                ? activeLinkPath
                : $"{activeLinkPath};{currentPath}";
            key.SetValue("Path", newPath, RegistryValueKind.ExpandString);
        }

        key.SetValue("DOTNET_ROOT", activeLinkPath, RegistryValueKind.ExpandString);
        BroadcastSettingChange();

        // Subprocesses (the post-switch smoke test cmd) inherit our PATH, not the registry's.
        // Without this, `cmd /c dotnet --version` can't find dotnet immediately after bootstrap.
        var procPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!procPath.Contains(activeLinkPath, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                string.IsNullOrEmpty(procPath) ? activeLinkPath : $"{activeLinkPath};{procPath}");
        }
        Environment.SetEnvironmentVariable("DOTNET_ROOT", activeLinkPath);

        return ValueTask.CompletedTask;
    }

    public async ValueTask CreateOrUpdateLinkAsync(string targetPath, CancellationToken ct = default)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullTarget))
        {
            throw new DirectoryNotFoundException($"Link target does not exist: '{fullTarget}'.");
        }

        if (!PathSafety.IsInsideRoot(DataDir, fullTarget) &&
            !IsInsideKnownSystemSdkRoot(fullTarget))
        {
            throw new InvalidOperationException(
                $"Refusing to create active link to '{fullTarget}': not under managed or system SDK root.");
        }

        Directory.CreateDirectory(DataDir);

        if (Directory.Exists(ActiveLinkPath))
        {
            // Junctions and symlinks are reparse points; Directory.Delete removes the link only,
            // not its target. Avoids spawning cmd /c rmdir.
            Directory.Delete(ActiveLinkPath, recursive: false);
        }
        else if (File.Exists(ActiveLinkPath))
        {
            File.Delete(ActiveLinkPath);
        }

        if (!CreateJunction(ActiveLinkPath, fullTarget, out var error))
        {
            // Fall back to mklink only after path validation has passed.
            var result = await _runner.RunAsync(
                "cmd",
                ["/c", "mklink", "/J", ActiveLinkPath, fullTarget],
                10,
                ct);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to create junction '{ActiveLinkPath}' -> '{fullTarget}': {error}; mklink fallback: {result.Stderr}");
            }
        }
    }

    public async ValueTask<string> RunInShellAsync(string command, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("cmd", ["/c", command], timeoutSeconds, ct);
        return result.Stdout;
    }

    public bool IsBootstrapped()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        if (key is null)
        {
            return false;
        }

        var path = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
        return path.Contains(ActiveLinkPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInsideKnownSystemSdkRoot(string path)
    {
        foreach (var root in SystemSdkScanPaths)
        {
            if (PathSafety.IsInsideRoot(root, path))
            {
                return true;
            }
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private static void BroadcastSettingChange()
    {
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(
            new IntPtr(-1), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 5000, out _);
    }

    private const int FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private static bool CreateJunction(string junctionPath, string targetDir, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(junctionPath);

            using var handle = CreateFileW(
                junctionPath,
                GENERIC_WRITE,
                0,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                error = $"CreateFile failed (Win32 error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            var substituteName = @"\??\" + targetDir.TrimEnd(Path.DirectorySeparatorChar);
            var printName = targetDir.TrimEnd(Path.DirectorySeparatorChar);

            var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
            var printBytes = System.Text.Encoding.Unicode.GetBytes(printName);
            var pathBufferSize = substituteBytes.Length + 2 + printBytes.Length + 2;
            var totalSize = 8 + 8 + pathBufferSize;

            var buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                Marshal.WriteInt32(buffer, 0, unchecked((int)IO_REPARSE_TAG_MOUNT_POINT));
                Marshal.WriteInt16(buffer, 4, (short)(8 + pathBufferSize));
                Marshal.WriteInt16(buffer, 6, 0);

                Marshal.WriteInt16(buffer, 8, 0);
                Marshal.WriteInt16(buffer, 10, (short)substituteBytes.Length);
                Marshal.WriteInt16(buffer, 12, (short)(substituteBytes.Length + 2));
                Marshal.WriteInt16(buffer, 14, (short)printBytes.Length);

                Marshal.Copy(substituteBytes, 0, buffer + 16, substituteBytes.Length);
                Marshal.WriteInt16(buffer, 16 + substituteBytes.Length, 0);
                Marshal.Copy(printBytes, 0, buffer + 16 + substituteBytes.Length + 2, printBytes.Length);
                Marshal.WriteInt16(buffer, 16 + substituteBytes.Length + 2 + printBytes.Length, 0);

                if (!DeviceIoControl(
                    handle, FSCTL_SET_REPARSE_POINT, buffer, (uint)totalSize,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    error = $"DeviceIoControl failed (Win32 error {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { Directory.Delete(junctionPath); } catch { /* best-effort */ }
            return false;
        }
    }
}
