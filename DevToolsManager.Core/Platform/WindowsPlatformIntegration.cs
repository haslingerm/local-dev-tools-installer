using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevToolsManager.Core.Process;
using DevToolsManager.Core.Util;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DevToolsManager.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformIntegration : IPlatformIntegration
{
    private readonly IProcessRunner _runner;
    private readonly string _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public WindowsPlatformIntegration(IProcessRunner runner) => _runner = runner;

    public string DataDir => Path.Combine(_localAppData, "DevToolsManager");
    public string InstallRoot => Path.Combine(DataDir, "sdks");
    public string ActiveLinkPath => Path.Combine(DataDir, "active");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string SideloadDir => Path.Combine(DataDir, "sideload");
    public string ArchiveExtension => ".zip";

    public string IdeInstallRoot => Path.Combine(DataDir, "ides");
    public string IdeSideloadDir => Path.Combine(DataDir, "sideload-ides");

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

    public async ValueTask CreateOrUpdateIdeLinkAsync(
        string productSlug,
        string targetPath,
        CancellationToken ct = default)
    {
        PathSafety.RequireValidFileName(productSlug, nameof(productSlug));

        var fullTarget = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullTarget))
        {
            throw new DirectoryNotFoundException($"Link target does not exist: '{fullTarget}'.");
        }

        if (!PathSafety.IsInsideRoot(IdeInstallRoot, fullTarget))
        {
            throw new InvalidOperationException(
                $"Refusing to create active IDE link to '{fullTarget}': not under managed IDE root.");
        }

        var productRoot = PathSafety.CombineSafe(IdeInstallRoot, productSlug);
        Directory.CreateDirectory(productRoot);
        var linkPath = Path.Combine(productRoot, "active");

        if (Directory.Exists(linkPath))
        {
            // Junctions are reparse points; Directory.Delete removes the link only,
            // not its target.
            Directory.Delete(linkPath, recursive: false);
        }
        else if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
        }

        if (!CreateJunction(linkPath, fullTarget, out var error))
        {
            var result = await _runner.RunAsync(
                "cmd",
                ["/c", "mklink", "/J", linkPath, fullTarget],
                10,
                ct);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to create junction '{linkPath}' -> '{fullTarget}': {error}; mklink fallback: {result.Stderr}");
            }
        }
    }

    public ValueTask CreateOrUpdateShortcutAsync(
        IdeShortcutSpec spec,
        CancellationToken ct = default)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(startMenu);
        var lnkPath = Path.Combine(startMenu, spec.DisplayName + ".lnk");

        var workingDir = Path.GetDirectoryName(spec.ExecutablePath) ?? "";

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            link.SetPath(spec.ExecutablePath);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(spec.Comment);
            link.SetIconLocation(spec.IconPath, 0);
            link.SetShowCmd(SW_SHOWNORMAL);
            ((IPersistFile)link).Save(lnkPath, fRemember: true);
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveShortcutAsync(
        string productSlug,
        string displayName,
        CancellationToken ct = default)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var lnkPath = Path.Combine(startMenu, displayName + ".lnk");
        if (File.Exists(lnkPath))
        {
            try { File.Delete(lnkPath); } catch { /* best-effort */ }
        }
        return ValueTask.CompletedTask;
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

    // ----- IShellLinkW COM interop for .lnk creation (no admin required) -----

    private const int SW_SHOWNORMAL = 1;
    private const int MAX_PATH = 260;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // ----- Junction reparse point creation -----

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
