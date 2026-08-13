// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable

namespace Svg.CodeGen.Skia.Projects;

/// <summary>What the output file receives.</summary>
public enum SvgEmit
{
    /// <summary>Generated C#, the drawing having been modelled first.</summary>
    CSharp,

    /// <summary>
    /// The document a recipe produced, written straight out. No scene model is built, so a
    /// drawing the renderer cannot handle still converts.
    /// </summary>
    Svg
}
