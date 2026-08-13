namespace svgc;

/// <summary>What the output file receives.</summary>
internal enum SvgEmit
{
    /// <summary>Generated C#, the drawing having been modelled first.</summary>
    CSharp,

    /// <summary>
    /// The document a recipe produced, written straight out. No scene model is built, so a
    /// drawing the renderer cannot handle still converts.
    /// </summary>
    Svg
}
