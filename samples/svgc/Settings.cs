namespace svgc;

internal class Settings
{
    public System.IO.FileInfo? InputFile { get; set; }
    public System.IO.FileInfo? OutputFile { get; set; }
    public System.IO.FileInfo? JsonFile { get; set; }
    public System.IO.FileInfo? RecipeFile { get; set; }
    // Named for their options rather than the ...File pattern above: System.CommandLine binds a
    // setting by matching the option name, so '--singleFile' has to land on 'SingleFile'.
    public System.IO.FileInfo? SingleFile { get; set; }
    public string? HelperScope { get; set; }
    public string? Cache { get; set; }
    public string? Emit { get; set; }
    public string Namespace { get; set; } = "Svg";
    public string Class { get; set; } = "Generated";
}
