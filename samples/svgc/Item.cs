namespace svgc;

internal class Item
{
    public string? InputFile { get; set; }
    public string? OutputFile { get; set; }
    public string? Namespace { get; set; }
    public string? Class { get; set; }

    /// <summary>Optional recipe, applied to the input before it is generated from.</summary>
    public string? Recipe { get; set; }

    /// <summary>Where to keep the converted document, when it is worth looking at.</summary>
    public string? EmitSvg { get; set; }
}
