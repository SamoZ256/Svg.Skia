namespace svgc;

internal class Settings
{
    public System.IO.FileInfo? InputFile { get; set; }
    public System.IO.FileInfo? OutputFile { get; set; }
    public System.IO.FileInfo? ProjectFile { get; set; }
    public System.IO.FileInfo? RecipeFile { get; set; }
    // Named for their options rather than the ...File pattern above: System.CommandLine binds a
    // setting by matching the option name, so '--singleFile' has to land on 'SingleFile'.
    public System.IO.FileInfo? SingleFile { get; set; }
    public string? HelperScope { get; set; }
    public string? Cache { get; set; }
    public string? Emit { get; set; }
    public string? SkiaSharp { get; set; }
    public string? Width { get; set; }
    public string? Height { get; set; }
    public string? Scale { get; set; }
    public string? Padding { get; set; }
    public string? Namespace { get; set; }
    public string? Class { get; set; }
}
