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
        bool byteOrderMark,
        SvgExpressionDeclarations declarations,
        string? declarationError)
    {
        Svg = svg;
        Path = path;
        SourceText = sourceText;
        ByteOrderMark = byteOrderMark;
        Declarations = declarations;
        DeclarationError = declarationError;
    }

    public SKSvg Svg { get; }

    public string? Path { get; }

    /// <summary>The drawing as it was read, or null if the source could not be kept.</summary>
    /// <remarks>
    /// Captured rather than re-read, so what is shown is the text the picture was built from.
    /// <see cref="SKSvg.CacheOriginalStream"/> would do it process-wide, which a viewer has no
    /// business turning on for every other <see cref="SKSvg"/> in the application.
    /// </remarks>
    public string? SourceText { get; }

    /// <summary>Whether the file this came from began with a byte order mark.</summary>
    /// <remarks>
    /// Both readers strip a mark and .NET writes UTF-8 without one, so a file that had one would
    /// lose three bytes on the first save.
    /// </remarks>
    public bool ByteOrderMark { get; }

    /// <summary>What the document declares, or empty when it declares nothing or the block is bad.</summary>
    public SvgExpressionDeclarations Declarations { get; }

    /// <summary>
    /// Why the declarations could not be read, or null.
    /// </summary>
    /// <remarks>
    /// Recorded rather than thrown: a document with a malformed <c>&lt;e:code&gt;</c> renders its
    /// placeholders perfectly well, and only the parameter panel needs the block.
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
        var mark = false;

        try
        {
            var bytes = File.ReadAllBytes(path);

            mark = HasByteOrderMark(bytes);
            source = DecodeUtf8(bytes);
        }
        catch (IOException)
        {
            // The drawing is open and rendering; not being able to show its text as well is not a
            // reason to fail the load.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Describe(svg, path, source, mark);
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

        var bytes = buffer.ToArray();

        return Describe(svg, path, DecodeUtf8(bytes), HasByteOrderMark(bytes));
    }

    /// <summary>The same drawing, rebuilt from edited text.</summary>
    /// <remarks>
    /// Through a stream and a base URI, not <see cref="SKSvg.FromSvg"/>, which has none: a drawing
    /// with an <c>&lt;image href="logo.png"&gt;</c> beside it loses the image the moment it is
    /// rebuilt from text — measured at the centre pixel, image colour to placeholder grey and back.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The text is not readable as SVG.</exception>
    public SvgViewerDocument Reload(string svgText)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        var svg = new SKSvg();

        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(svgText));

        if (svg.Load(buffer, null, BaseUri()) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException("The text could not be read as SVG.");
        }

        return Describe(svg, Path, svgText, ByteOrderMark);
    }

    /// <summary>Writes text to a file, in the encoding this drawing was read in.</summary>
    /// <remarks>
    /// The encoding it arrived in, so a byte order mark survives. Whether to write is a host's
    /// decision; how the bytes came in is only known here.
    /// </remarks>
    public void Write(string svgText, string path)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        File.WriteAllText(path, svgText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: ByteOrderMark));
    }

    /// <summary>What a relative reference resolves against, or null when there is no file.</summary>
    private Uri? BaseUri()
    {
        if (Path is null)
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));

        return directory is null ? null : new Uri(directory + System.IO.Path.DirectorySeparatorChar);
    }

    private static bool HasByteOrderMark(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>Reads bytes as text, honouring a byte order mark if there is one.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static SvgViewerDocument Describe(SKSvg svg, string? path, string? sourceText, bool byteOrderMark = false)
    {
        SvgExpressionDeclarations declarations;
        string? error = null;

        try
        {
            declarations = svg.ExpressionDeclarations;
        }
        catch (ExprException failure)
        {
            declarations = SvgExpressionDeclarations.Empty;
            error = failure.ToDiagnostic();
        }

        return new SvgViewerDocument(svg, path, sourceText, byteOrderMark, declarations, error);
    }

    public void Dispose() => Svg.Dispose();
}
