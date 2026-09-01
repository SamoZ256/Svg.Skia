// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Svg.Expressions;
using Svg.Skia;
using Svg.SourceEditing;

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
        string? declarationError,
        Func<string, string>? rewrite)
    {
        Svg = svg;
        Path = path;
        SourceText = sourceText;
        ByteOrderMark = byteOrderMark;
        Declarations = declarations;
        DeclarationError = declarationError;
        Rewrite = rewrite;
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

    /// <summary>What the text goes through on its way to being drawn, or null when it is drawn as written.</summary>
    /// <remarks>
    /// For a host whose drawing is derived from the file rather than being it — an svgc project
    /// applying a recipe. Held here rather than by the caller so that every rebuild goes through it,
    /// and <see cref="SourceText"/> stays the file's own: the pane shows, edits and saves the file,
    /// not what was made of it.
    /// </remarks>
    public Func<string, string>? Rewrite { get; }

    /// <summary>The text as this drawing is built from it, which is the text itself without a rewrite.</summary>
    public string Built(string svgText) => Rewrite is { } rewrite ? rewrite(svgText) : svgText;

    public static SvgViewerDocument Load(string path) => Load(path, SvgSizeRequest.None);

    /// <summary>
    /// The drawing at <paramref name="path"/>, built at the size <paramref name="request"/> asks for.
    /// </summary>
    /// <remarks>
    /// The size is applied to the parsed document rather than to its text, so the file keeps the size
    /// it was written with. That is the difference from <see cref="Resize"/>: a project's
    /// <c>scale="2"</c> says how to build a drawing, not what the drawing is, and writing it into the
    /// file would make the next build square it.
    /// </remarks>
    public static SvgViewerDocument Load(string path, SvgSizeRequest request)
        => Load(path, request, null);

    /// <summary>The drawing at <paramref name="path"/>, built from what <paramref name="rewrite"/> makes of it.</summary>
    /// <remarks><see cref="Rewrite"/> says what that is for.</remarks>
    public static SvgViewerDocument Load(string path, SvgSizeRequest request, Func<string, string>? rewrite)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        string? source = null;
        var mark = false;

        // Read before the drawing is built rather than after, because a rewrite builds from this.
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

        // Before the SKSvg exists, so a rewrite that refuses the drawing does not leak one.
        var rewritten = rewrite is { } && source is { } ? rewrite(source) : null;

        var svg = new SKSvg();

        var built = rewritten is { }
            // Through a stream and the file's directory, the way Reload does: what is drawn is not
            // the file, so it cannot be loaded from the path, and its relative references still
            // have to resolve against where the file is.
            ? Text(svg, request, rewritten, BaseUri(path))
            // Loaded from the path rather than from the text above, because a document's relative
            // references — an <image href="…"> beside it — resolve against the file's own directory.
            : Build(svg, request, () => global::Svg.Model.Services.SvgService.Open(path), () => svg.Load(path));

        if (built is null)
        {
            svg.Dispose();
            throw new InvalidOperationException($"'{System.IO.Path.GetFileName(path)}' could not be read as SVG.");
        }

        return Describe(svg, path, source, mark, rewrite);
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
    public SvgViewerDocument Reload(string svgText) => Reload(svgText, SvgSizeRequest.None);

    /// <summary>The same drawing, rebuilt from edited text at the size <paramref name="request"/> asks for.</summary>
    public SvgViewerDocument Reload(string svgText, SvgSizeRequest request)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        var built = Built(svgText);

        var svg = new SKSvg();

        if (Text(svg, request, built, BaseUri()) is null)
        {
            svg.Dispose();
            throw new InvalidOperationException("The text could not be read as SVG.");
        }

        // The text, not what was built from it: this document's source is still the file's own.
        return Describe(svg, Path, svgText, ByteOrderMark, Rewrite);
    }

    /// <summary>Builds <paramref name="svg"/> from text, resolving what it references against <paramref name="baseUri"/>.</summary>
    private static object? Text(SKSvg svg, SvgSizeRequest request, string svgText, Uri? baseUri)
    {
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(svgText));

        return Build(
            svg,
            request,
            () => global::Svg.Model.Services.SvgService.Open(Reader(buffer, baseUri)),
            () => svg.Load(buffer, null, baseUri));
    }

    /// <summary>
    /// Builds <paramref name="svg"/>, resizing it first when there is a size to apply.
    /// </summary>
    /// <remarks>
    /// Two paths because the plain one is the loader's own and knows about formats, streams and base
    /// URIs; only a request worth honouring is worth parsing separately for, and a request the sizing
    /// model refuses leaves the drawing at its natural size rather than failing the load — being
    /// unable to resize is not being unable to read.
    /// </remarks>
    private static object? Build(SKSvg svg, SvgSizeRequest request, Func<SvgDocument?> parse, Func<object?> plain)
    {
        if (request.IsEmpty)
        {
            return plain();
        }

        if (parse() is not { } document)
        {
            return null;
        }

        try
        {
            SvgSceneSizing.Apply(document, svg.AssetLoader, request);
        }
        catch (ArgumentException)
        {
            // Nothing to measure, or a size this drawing cannot take.
        }

        return svg.FromSvgDocument(document);
    }

    /// <summary>A reader over <paramref name="buffer"/> that resolves relative references against <paramref name="baseUri"/>.</summary>
    private static XmlReader Reader(MemoryStream buffer, Uri? baseUri)
    {
        buffer.Position = 0;

        return XmlReader.Create(
            buffer,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null },
            baseUri?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// The edits that resize <paramref name="svgText"/> as <paramref name="request"/> asks.
    /// </summary>
    /// <remarks>
    /// The arithmetic is <see cref="SvgSceneSizing"/>'s, which is the same one svgc resizes by, and
    /// all it writes is the root's width, height and viewBox. So the request is applied to a
    /// throwaway document and those three values are read back off it and written into the text as
    /// spans — the author's formatting, attribute order and comments are none of a resize's
    /// business, and regenerating the markup from the parsed tree would lose all three.
    /// </remarks>
    /// <returns>What to edit, or why the drawing cannot be resized.</returns>
    public SvgSourceEditResult Resize(string svgText, SvgSizeRequest request)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (request.IsEmpty)
        {
            return SvgSourceEditResult.Nothing;
        }

        // Qualified, because Svg is also the property holding this document's picture.
        var resized = global::Svg.Model.Services.SvgService.FromSvg(svgText);

        if (resized is null)
        {
            return SvgSourceEditResult.Refuse("This drawing cannot be read as SVG yet, so there is no size to change.");
        }

        var before = resized.ViewBox;

        try
        {
            SvgSceneSizing.Apply(resized, Svg.AssetLoader, request);
        }
        catch (ArgumentException failure)
        {
            // Nothing to measure, or a request the sizing model refuses. Both are answers to give
            // back rather than faults: the caller asked for something this drawing cannot do.
            return SvgSourceEditResult.Refuse(failure.Message);
        }

        var after = resized.ViewBox;

        return SvgFrameEditor.SetFrame(
            svgText,
            resized.Width.ToString(),
            resized.Height.ToString(),
            // Only where the resize decided one — it adds a viewBox to a document without one, and
            // reframes for padding. An author's own, left alone by the resize, keeps the spacing it
            // was written with rather than being reformatted to say the same thing.
            after == before ? null : Frame(after));
    }

    private static string Frame(SvgViewBox viewBox)
        => string.Join(
            " ",
            viewBox.MinX.ToSvgString(),
            viewBox.MinY.ToSvgString(),
            viewBox.Width.ToSvgString(),
            viewBox.Height.ToSvgString());

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
    private Uri? BaseUri() => BaseUri(Path);

    private static Uri? BaseUri(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));

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

    private static SvgViewerDocument Describe(
        SKSvg svg,
        string? path,
        string? sourceText,
        bool byteOrderMark = false,
        Func<string, string>? rewrite = null)
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

        return new SvgViewerDocument(svg, path, sourceText, byteOrderMark, declarations, error, rewrite);
    }

    public void Dispose() => Svg.Dispose();
}
