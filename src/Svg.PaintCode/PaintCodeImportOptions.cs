// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode;

/// <summary>What an import writes, and where.</summary>
public sealed class PaintCodeImportOptions
{
    public PaintCodeImportOptions(string directory)
    {
        Directory = directory;
    }

    /// <summary>The directory the drawings are written under, a folder per desk.</summary>
    public string Directory { get; }

    /// <summary>
    /// Whether a canvas that exists only to be used as a symbol gets a drawing of its own.
    /// </summary>
    /// <remarks>
    /// Off, because a project row for one would have code generated for it that nothing calls; a
    /// symbol reaches the drawings that use it whether or not it is written out itself.
    /// </remarks>
    public bool IncludeSymbolOnlyCanvases { get; set; }

    /// <summary>
    /// Whether a whole-valued number variable is written as an <c>integer</c> parameter.
    /// </summary>
    /// <remarks>
    /// Off, and a guess when it is on. PaintCode has three numeric kinds -- Number, Fraction and
    /// Angle -- and none of them is whole: a value is archived as a real whether or not it looks like
    /// an integer, and a variable's bounds are a min and a max with no step, so nothing in the format
    /// distinguishes a step enum from a slider that happens to sit on a whole number.
    ///
    /// What this is for is the shape the importer already produces and cannot say better: a
    /// parameter compared with <c>==</c> against 0, 1, 2 and so on, which is an integer written as a
    /// float. Turning it on retypes those, and will also retype a Number slider whose ends and value
    /// are whole -- which is why the author asks for it rather than being given it. A Fraction and an
    /// Angle are never retyped, being continuous by declaration.
    /// </remarks>
    public bool Integers { get; set; }

    /// <summary>Where the svgc project is written, or null to write none.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>The namespace the project's generated code sits in; each desk adds its own below it.</summary>
    public string? Namespace { get; set; }
}
