namespace svgc;

internal class Settings
{
    public System.IO.FileInfo? InputFile { get; set; }
    public System.IO.FileInfo? OutputFile { get; set; }
    public System.IO.FileInfo? JsonFile { get; set; }
    public System.IO.FileInfo? RecipeFile { get; set; }
    // Named for its option rather than the ...File pattern above: System.CommandLine binds a
    // setting by matching the option name, so '--emitSvg' has to land on 'EmitSvg'.
    public System.IO.FileInfo? EmitSvg { get; set; }
    public string Namespace { get; set; } = "Svg";
    public string Class { get; set; } = "Generated";
}
