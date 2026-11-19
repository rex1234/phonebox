using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PhoneBox.Agent;

/// <summary>
/// The computer's current desktop wallpaper, for the panel shown on this computer (the panel adds <c>?wallpaper=1</c> to
/// its address and draws the picture behind its cards). Windows only. The file is the wallpaper's path from the registry
/// (an image of a known type, at most <see cref="MaxBytes"/>), else the copy Windows keeps as TranscodedWallpaper; a request
/// never names a path, so nothing else can be read this way. Served on the local port only (see Program.cs).
/// </summary>
public static class Wallpaper
{
    internal const long MaxBytes = 30 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".gif"] = "image/gif",
    };

    /// <summary>
    /// The file to serve: the configured wallpaper when it is an absolute path to an existing image of an allowed type and
    /// size, else the transcoded copy (a JPEG without an extension) when it exists and is not too large, else null.
    /// </summary>
    internal static string? Pick(string? configured, string? transcoded, Func<string, long?> sizeOf)
    {
        bool Fits(string path) => sizeOf(path) is > 0 and var n && n <= MaxBytes;
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
            && Types.ContainsKey(Path.GetExtension(configured)) && Fits(configured)) return configured;
        return !string.IsNullOrWhiteSpace(transcoded) && Fits(transcoded) ? transcoded : null;
    }

    /// <summary>The media type of a picked file: by extension, and a JPEG for the extension-less transcoded copy.</summary>
    internal static string ContentType(string path) =>
        Types.TryGetValue(Path.GetExtension(path), out var type) ? type : "image/jpeg";

    /// <summary>The wallpaper file to serve now, or null (not Windows, a solid colour, no readable file).</summary>
    public static string? Current()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? configured = null;
        try { configured = RegistryPath(); } catch { }
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return Pick(configured, transcoded, SizeOf);
    }

    [SupportedOSPlatform("windows")]
    private static string? RegistryPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("WallPaper") as string;
    }

    private static long? SizeOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : null; }
        catch { return null; }
    }
}
