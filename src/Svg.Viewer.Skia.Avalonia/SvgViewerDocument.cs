// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Svg.Expressions;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// One loaded drawing: the renderer, what it declares, and anything wrong with the declarations.
/// </summary>
/// <remarks>
/// Loading is deliberately separable from the controls, because parsing a document and compiling its
/// scene is the expensive half and belongs off the UI thread, while everything after it does not.
/// </remarks>
public sealed class SvgViewerDocument : IDisposable
{
    private SvgViewerDocument(
        SKSvg svg,
        string? path,
        IReadOnlyList<SvgExpressionParameter> declarations,
        string? declarationError)
    {
        Svg = svg;
        Path = path;
        Declarations = declarations;
        DeclarationError = declarationError;
    }

    public SKSvg Svg { get; }

    public string? Path { get; }

    /// <summary>What the document declares, or empty when it declares nothing or the block is bad.</summary>
    public IReadOnlyList<SvgExpressionParameter> Declarations { get; }

    /// <summary>
    /// Why the declarations could not be read, or null.
    /// </summary>
    /// <remarks>
    /// Recorded rather than thrown, because loading deliberately does not read declarations: a
    /// document with a malformed <c>&lt;e:code&gt;</c> renders its placeholders perfectly well, and
    /// refusing to show it would throw away the drawing over a block that only the parameter panel
    /// needs.
    /// </remarks>
    public string? DeclarationError { get; }

    public static SvgViewerDocument Load(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        var svg = new SKSvg();

        if (svg.Load(path) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException($"'{System.IO.Path.GetFileName(path)}' could not be read as SVG.");
        }

        return Describe(svg, path);
    }

    public static SvgViewerDocument LoadFromSvg(string svgText, string? path = null)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        var svg = new SKSvg();

        if (svg.FromSvg(svgText) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException("The text could not be read as SVG.");
        }

        return Describe(svg, path);
    }

    public static SvgViewerDocument Load(Stream stream, string? path = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var svg = new SKSvg();

        if (svg.Load(stream) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException("The stream could not be read as SVG.");
        }

        return Describe(svg, path);
    }

    private static SvgViewerDocument Describe(SKSvg svg, string? path)
    {
        IReadOnlyList<SvgExpressionParameter> declarations;
        string? error = null;

        try
        {
            declarations = svg.ExpressionParameters;
        }
        catch (ExprException failure)
        {
            declarations = Array.Empty<SvgExpressionParameter>();
            error = failure.ToDiagnostic();
        }

        return new SvgViewerDocument(svg, path, declarations, error);
    }

    public void Dispose() => Svg.Dispose();
}
