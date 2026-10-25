using System;
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
    /// That git can still be writing objects, so the read-only flags are cleared again on every try.
    /// </remarks>
    public static void Delete(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
