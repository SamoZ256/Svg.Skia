using System.IO;
using System.Threading;

namespace Svg.Studio.UnitTests;

/// <summary>The temporary directories the tests write projects and repositories into.</summary>
internal static class Scratch
{
    /// <summary>Deletes <paramref name="directory"/> and everything in it, as Windows allows.</summary>
    /// <remarks>
    /// Windows refuses to delete a read-only file, which is how git writes its objects, and a directory
    /// that is a live process's working directory — the git a drop starts without anything to await it.
    /// </remarks>
    public static void Delete(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
