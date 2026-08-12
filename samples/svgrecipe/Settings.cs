#nullable enable
using System.IO;

namespace svgrecipe;

public class Settings
{
    public FileInfo? InputFile { get; set; }

    public FileInfo? RecipeFile { get; set; }

    public FileInfo? OutputFile { get; set; }

    public bool Quiet { get; set; }
}
