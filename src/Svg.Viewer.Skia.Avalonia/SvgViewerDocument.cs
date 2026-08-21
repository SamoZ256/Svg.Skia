// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        string? sourceText,
        IReadOnlyList<SvgExpressionParameter> declarations,
        string? declarationError)
    {
        Svg = svg;
        Path = path;
        SourceText = sourceText;
        Declarations = declarations;
        DeclarationError = declarationError;
    }

    public SKSvg Svg { get; }

    public string? Path { get; }

    /// <summary>The drawing as it was read, or null if the source could not be kept.</summary>
    /// <remarks>
    /// Captured here rather than re-read on demand, so what is shown is the text the picture was
    /// built from and not whatever the file says later. <see cref="SKSvg"/> can retain its own
    /// source, but only behind the process-wide <see cref="SKSvg.CacheOriginalStream"/> toggle it
    /// keeps for reloading — a viewer has no business making every other <see cref="SKSvg"/> in the
    /// application hold a copy of its file.
    /// </remarks>
    public string? SourceText { get; }

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

        // Loaded from the path rather than from the text below, because a document's relative
        // references — an <image href="…"> beside it — resolve against the file's own directory.
        if (svg.Load(path) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException($"'{System.IO.Path.GetFileName(path)}' could not be read as SVG.");
        }

        string? source = null;

        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // The drawing is open and rendering; not being able to show its text as well is not a
            // reason to fail the load.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Describe(svg, path, source);
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

        return Describe(svg, path, svgText);
    }

    public static SvgViewerDocument Load(Stream stream, string? path = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        // Buffered because the loader consumes the stream, and the text has to come from the same
        // bytes the picture was built from — a stream cannot be read twice.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        var svg = new SKSvg();

        if (svg.Load(buffer) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException("The stream could not be read as SVG.");
        }

        return Describe(svg, path, DecodeUtf8(buffer.ToArray()));
    }

    /// <summary>Reads bytes as text, honouring a byte order mark if there is one.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static SvgViewerDocument Describe(SKSvg svg, string? path, string? sourceText)
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

        return new SvgViewerDocument(svg, path, sourceText, declarations, error);
    }

    public void Dispose() => Svg.Dispose();
}
