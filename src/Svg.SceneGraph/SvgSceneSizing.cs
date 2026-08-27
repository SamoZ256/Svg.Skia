using System;
using ShimSkiaSharp;
using Svg;
using Svg.Model;
using Svg.Model.Services;

namespace Svg.Skia;

/// <summary>
/// A resize asked of a drawing: an explicit size in pixels, or a factor of the size the document
/// already has.
/// </summary>
/// <remarks>
/// The three are one group rather than three independent settings. A scale and an explicit size
/// are two ways of saying the same thing, so they cannot both apply, and a caller that overrides
/// any of them overrides all of them.
/// </remarks>
public readonly struct SvgSizeRequest
{
    public SvgSizeRequest(float? width, float? height, float? scale)
    {
        if (scale is { } && (width is { } || height is { }))
        {
            throw new ArgumentException(
                "A scale and an explicit width or height are two ways of asking for the same thing. Give one or the other.");
        }

        Require(width, "width");
        Require(height, "height");
        Require(scale, "scale");

        Width = width;
        Height = height;
        Scale = scale;
    }

    /// <summary>The width to resize to, in pixels.</summary>
    public float? Width { get; }

    /// <summary>The height to resize to, in pixels.</summary>
    public float? Height { get; }

    /// <summary>The factor to resize by, against the size the document already has.</summary>
    public float? Scale { get; }

    /// <summary>No resize: the drawing keeps the size its document gives it.</summary>
    public static SvgSizeRequest None => default;

    public bool IsEmpty => Width is null && Height is null && Scale is null;

    /// <summary>
    /// The size this request asks for, given the one the document already has.
    /// </summary>
    /// <remarks>
    /// One dimension derives the other, so an aspect ratio is never changed by halves. Both given is
    /// a box to fit into, which <c>preserveAspectRatio</c> centres the drawing in.
    /// </remarks>
    public SKSize Resolve(SKSize natural)
    {
        if (natural.Width <= 0f || natural.Height <= 0f)
        {
            throw new ArgumentException($"A drawing of {natural.Width}x{natural.Height} has no size to resize from.");
        }

        return this switch
        {
            { Scale: { } scale } => new SKSize(natural.Width * scale, natural.Height * scale),
            { Width: { } width, Height: { } height } => new SKSize(width, height),
            { Width: { } width } => new SKSize(width, width * natural.Height / natural.Width),
            { Height: { } height } => new SKSize(height * natural.Width / natural.Height, height),
            _ => natural
        };
    }

    private static void Require(float? value, string name)
    {
        if (value is { } number && (number <= 0f || float.IsNaN(number) || float.IsInfinity(number)))
        {
            throw new ArgumentException($"A {name} has to be a positive number, but was {number}.");
        }
    }
}

/// <summary>Resizes a document before it is compiled, rather than scaling what it compiled to.</summary>
/// <remarks>
/// A width and height against a viewBox are what size an SVG, so changing them is the whole of a
/// resize: the compiler folds the transform into path geometry where it can, rather than wrapping
/// the picture in a scale. What it buys over scaling is <c>preserveAspectRatio</c> — how a drawing
/// meets a box that is not its own shape — which a wrapped scale would have to work out by hand.
/// </remarks>
public static class SvgSceneSizing
{
    /// <summary>
    /// Resizes <paramref name="fragment"/> in place, so that compiling it afterwards produces the
    /// requested size. Does nothing if the request is empty.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Nothing could be measured to resize from: the document names no width, height or viewBox,
    /// and compiling it yielded no bounds either.
    /// </exception>
    public static void Apply(SvgFragment fragment, ISvgAssetLoader assetLoader, SvgSizeRequest request)
    {
        if (fragment is null)
        {
            throw new ArgumentNullException(nameof(fragment));
        }

        if (request.IsEmpty)
        {
            return;
        }

        var natural = GetNaturalBounds(fragment, assetLoader);
        var target = request.Resolve(natural.Size);

        // Without a viewBox, width and height are only a viewport and a larger one reframes rather
        // than resizes. One taken from the document's own size turns them into a scale.
        if (!HasViewBox(fragment))
        {
            fragment.ViewBox = new SvgViewBox(natural.Left, natural.Top, natural.Width, natural.Height);
        }

        fragment.Width = new SvgUnit(SvgUnitType.User, target.Width);
        fragment.Height = new SvgUnit(SvgUnitType.User, target.Height);
    }

    /// <summary>What the drawing covers today, in its own units.</summary>
    private static SKRect GetNaturalBounds(SvgFragment fragment, ISvgAssetLoader assetLoader)
    {
        var size = SvgService.GetDimensions(fragment);
        if (size.Width > 0f && size.Height > 0f)
        {
            return SKRect.Create(size);
        }

        if (HasViewBox(fragment))
        {
            return SKRect.Create(
                fragment.ViewBox.MinX,
                fragment.ViewBox.MinY,
                fragment.ViewBox.Width,
                fragment.ViewBox.Height);
        }

        // A document that names no size at all is framed by what it draws, which is only known
        // once it has been compiled. This costs a second compile, and only here.
        return SvgSceneRuntime.CreateModel(fragment, assetLoader)?.CullRect ?? SKRect.Empty;
    }

    private static bool HasViewBox(SvgFragment fragment)
        => !fragment.ViewBox.Equals(SvgViewBox.Empty) &&
           fragment.ViewBox.Width > 0f &&
           fragment.ViewBox.Height > 0f;
}
