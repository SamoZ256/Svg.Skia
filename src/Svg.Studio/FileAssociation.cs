using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Svg.Studio;

/// <summary>Makes a double-clicked <c>.svgstudio</c> open here, on Windows and Linux.</summary>
/// <remarks>
/// There is no installer to do this, so a launch does it, for the user only and pointing at whichever
/// copy ran last — moving the folder is mended by running it once from the new place. macOS reads the
/// association from the bundle's Info.plist instead. A debug build leaves it alone, or working on
/// Studio would hand everybody's projects to whichever bin folder was run last.
/// </remarks>
internal static class FileAssociation
{
    private const string Extension = ".svgstudio";
    private const string ProgId = "SvgStudio.Project";
    private const string MimeType = "application/x-svgstudio";
    private const string DesktopFile = "svg-studio.desktop";

    public static void Register()
    {
#if !DEBUG
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                RegisterWindows(executable);
            }
            else if (OperatingSystem.IsLinux())
            {
                RegisterLinux(executable);
            }
        }
        catch (Exception exception)
        {
            // Never a reason not to start; the file still opens through the Open panel.
            Trace.WriteLine($"Registering {Extension} failed: {exception.Message}");
        }
#endif
    }

    /// <remarks>
    /// Under HKCU\Software\Classes, which needs no elevation. Somebody who picked another program
    /// with "Open with" keeps it: Explorer's UserChoice outranks these keys.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindows(string executable)
    {
        var command = $"\"{executable}\" \"%1\"";

        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using (var open = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
        {
            if (open?.GetValue(null) as string == command)
            {
                return;
            }
        }

        using (var extension = classes.CreateSubKey(Extension))
        {
            extension.SetValue(null, ProgId);
        }

        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue(null, "Svg Studio Project");

            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{executable}\",0");

            using var open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        // Without it Explorer goes on showing the old icon and handler until it restarts.
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <remarks>
    /// A MIME type of its own, since a .svgstudio is otherwise just XML and claiming XML would take
    /// every XML file. The caches are rebuilt only when a file changed, so a launch that finds
    /// everything in place spawns nothing.
    /// </remarks>
    private static void RegisterLinux(string executable)
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var mime = Path.Combine(data, "mime");
        var applications = Path.Combine(data, "applications");

        var mimeChanged = Write(Path.Combine(mime, "packages", "svgstudio.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
              <mime-type type="{MimeType}">
                <comment>Svg Studio Project</comment>
                <sub-class-of type="application/xml"/>
                <glob pattern="*{Extension}"/>
              </mime-type>
            </mime-info>

            """);

        // The Exec key's own quoting: inside double quotes, ", `, $ and \ are escaped with a backslash.
        var quoted = executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        var desktopChanged = Write(Path.Combine(applications, DesktopFile), $"""
            [Desktop Entry]
            Type=Application
            Name=Svg Studio
            Exec="{quoted}" %f
            MimeType={MimeType};
            Categories=Graphics;VectorGraphics;
            Terminal=false

            """);

        if (mimeChanged)
        {
            Run("update-mime-database", mime);
        }

        if (desktopChanged)
        {
            Run("update-desktop-database", applications);
        }

        if (string.IsNullOrWhiteSpace(Run("xdg-mime", "query", "default", MimeType)))
        {
            Run("xdg-mime", "default", DesktopFile, MimeType);
        }
    }

    /// <summary>Writes <paramref name="text"/> unless the file already holds it, and says whether it wrote.</summary>
    private static bool Write(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);

        return true;
    }

    /// <summary>Runs a desktop tool and returns what it printed, or null where it is not installed.</summary>
    private static string? Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, UseShellExecute = false };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
