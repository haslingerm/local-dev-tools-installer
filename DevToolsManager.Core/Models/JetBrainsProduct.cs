namespace DevToolsManager.Core.Models;

/// <summary>
/// JetBrains IDE products this tool can install. Adding a new product is one
/// enum entry plus the corresponding rows in <see cref="JetBrainsProductInfo"/>.
/// </summary>
public enum JetBrainsProduct
{
    Rider,
    WebStorm,
}

/// <summary>
/// Per-product metadata. All callers should go through this table — neither
/// the catalog client, the installer, nor the UI should hard-code product
/// codes / display names elsewhere.
/// </summary>
public static class JetBrainsProductInfo
{
    /// <summary>Code used by the JetBrains data-services releases endpoint.</summary>
    public static string Code(JetBrainsProduct p) => p switch
    {
        JetBrainsProduct.Rider => "RD",
        JetBrainsProduct.WebStorm => "WS",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Lowercase identifier used in filesystem layout: <c>ides/&lt;slug&gt;/...</c>.</summary>
    public static string Slug(JetBrainsProduct p) => p switch
    {
        JetBrainsProduct.Rider => "rider",
        JetBrainsProduct.WebStorm => "webstorm",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>User-facing name (Start Menu, .desktop Name=, UI labels).</summary>
    public static string DisplayName(JetBrainsProduct p) => p switch
    {
        JetBrainsProduct.Rider => "JetBrains Rider",
        JetBrainsProduct.WebStorm => "JetBrains WebStorm",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Tagline shown as the shortcut comment / tooltip.</summary>
    public static string Comment(JetBrainsProduct p) => p switch
    {
        JetBrainsProduct.Rider => "The cross-platform .NET IDE",
        JetBrainsProduct.WebStorm => "The smart JavaScript IDE",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>WM_CLASS the IDE sets on its X11/Wayland window — used in Linux .desktop StartupWMClass.</summary>
    public static string StartupWmClass(JetBrainsProduct p) => p switch
    {
        JetBrainsProduct.Rider => "jetbrains-rider",
        JetBrainsProduct.WebStorm => "jetbrains-webstorm",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Path inside the extracted IDE root to the launcher, picked by current OS.</summary>
    public static string LauncherForCurrentOs(JetBrainsProduct p)
    {
        if (OperatingSystem.IsWindows())
        {
            return p switch
            {
                JetBrainsProduct.Rider => "bin/rider64.exe",
                JetBrainsProduct.WebStorm => "bin/webstorm64.exe",
                _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
            };
        }
        return p switch
        {
            JetBrainsProduct.Rider => "bin/rider.sh",
            JetBrainsProduct.WebStorm => "bin/webstorm.sh",
            _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
        };
    }

    /// <summary>
    /// Path inside the extracted IDE root to the icon, picked by current OS.
    /// Windows uses the embedded icon in the launcher .exe (IconLocation supports this).
    /// </summary>
    public static string IconForCurrentOs(JetBrainsProduct p)
    {
        if (OperatingSystem.IsWindows())
        {
            return LauncherForCurrentOs(p);
        }
        return p switch
        {
            JetBrainsProduct.Rider => "bin/rider.png",
            JetBrainsProduct.WebStorm => "bin/webstorm.png",
            _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
        };
    }

    public static IReadOnlyList<JetBrainsProduct> All { get; } =
        Enum.GetValues<JetBrainsProduct>();
}
