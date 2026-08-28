using System;
using System.Globalization;
using ShimSkiaSharp;
using Svg;
using Svg.Model;
using Svg.Model.Services;

namespace Svg.Skia;

/// <summary>
/// Space to leave around a drawing, as fractions of the size it is being resized to.
/// </summary>
/// <remarks>
/// Fractions rather than pixels, so one setting fits every target: a tenth of a 24px icon and a
/// tenth of a 512px one are both a tenth. It is space added outside the frame the document declares
/// and never a crop — slack an author already left is theirs, and this only ever adds to it.
/// </remarks>
public readonly struct SvgPadding
{
    public SvgPadding(float top, float right, float bottom, float left)
    {
        Require(top, "top");
        Require(right, "right");
        Require(bottom, "bottom");
        Require(left, "left");

        if (left + right >= 1f || top + bottom >= 1f)
        {
            throw new ArgumentException(
                $"A padding of {Percent(top)} and {Percent(bottom)} down, {Percent(left)} and {Percent(right)} across, leaves the drawing no room in the size it is being given.");
        }

        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public float Top { get; }

    public float Right { get; }

    public float Bottom { get; }

    public float Left { get; }

    /// <summary>No padding: the drawing meets the edges of the size it is given.</summary>
    public static SvgPadding None => default;

    public bool IsEmpty => Top == 0f && Right == 0f && Bottom == 0f && Left == 0f;

    /// <summary>
    /// Reads a padding written the way CSS writes one: one, two, three or four values.
    /// </summary>
    /// <remarks>
    /// The CSS order, because that is the order anybody writing four numbers for four sides already
    /// has in their head. A value is <c>10%</c> or the same thing as a fraction, <c>0.1</c>; a bare
    /// number is the fraction rather than the percentage, so <c>10</c> asks for ten times the canvas
    /// and is refused instead of quietly meaning a tenth of it.
    /// </remarks>
    public static SvgPadding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var parts = text!.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

        var sides = new float[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            sides[index] = Side(parts[index]);
        }

        return parts.Length switch
        {
            1 => new SvgPadding(sides[0], sides[0], sides[0], sides[0]),
            2 => new SvgPadding(sides[0], sides[1], sides[0], sides[1]),
            3 => new SvgPadding(sides[0], sides[1], sides[2], sides[1]),
            4 => new SvgPadding(sides[0], sides[1], sides[2], sides[3]),
            _ => throw new ArgumentException(
                $"A padding takes one, two, three or four values the way CSS does, but '{text}' has {parts.Length}.")
        };
    }

    private static float Side(string text)
    {
        var percent = text.EndsWith("%", StringComparison.Ordinal);
        var number = percent ? text.Substring(0, text.Length - 1) : text;

        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"'{text}' is not a padding. Write one as 10% or as the fraction 0.1.");
        }

        return percent ? value / 100f : value;
    }

    private static string Percent(float value)
        => (value * 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static void Require(float value, string name)
    {
        if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new ArgumentException(
                $"A {name} padding has to be zero or more, but was {value}. Padding adds space and never takes it away.");
        }
    }
}

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
        : this(width, height, scale, SvgPadding.None)
    {
    }

    public SvgSizeRequest(float? width, float? height, float? scale, SvgPadding padding)
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
        Padding = padding;
    }

    /// <summary>The width to resize to, in pixels.</summary>
    public float? Width { get; }

    /// <summary>The height to resize to, in pixels.</summary>
    public float? Height { get; }

    /// <summary>The factor to resize by, against the size the document already has.</summary>
    public float? Scale { get; }

    /// <summary>Space to leave around the drawing inside the size it is given.</summary>
    public SvgPadding Padding { get; }

    /// <summary>No resize: the drawing keeps the size its document gives it.</summary>
    public static SvgSizeRequest None => default;

    /// <summary>
    /// Whether nothing is being asked for at all.
    /// </summary>
    /// <remarks>
    /// Padding counts. It is not a resize, but it is the same kind of thing to a caller deciding
    /// whether a document has to be reframed before it is compiled — including the one that refuses
    /// to combine either with emitting SVG.
    /// </remarks>
    public bool IsEmpty => Width is null && Height is null && Scale is null && Padding.IsEmpty;

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

        if (request.Padding.IsEmpty)
        {
            // Without a viewBox, width and height are only a viewport and a larger one reframes rather
            // than resizes. One taken from the document's own size turns them into a scale.
            if (!HasViewBox(fragment))
            {
                fragment.ViewBox = new SvgViewBox(natural.Left, natural.Top, natural.Width, natural.Height);
            }
        }
        else
        {
            fragment.ViewBox = Frame(ContentFrame(fragment, natural), target, request.Padding);
        }

        fragment.Width = new SvgUnit(SvgUnitType.User, target.Width);
        fragment.Height = new SvgUnit(SvgUnitType.User, target.Height);
    }

    /// <summary>
    /// A viewBox that leaves <paramref name="padding"/> of <paramref name="target"/> clear on each
    /// side of <paramref name="content"/>.
    /// </summary>
    /// <remarks>
    /// Written as a viewBox rather than asked of <c>preserveAspectRatio</c>, which offers nine
    /// alignments and no offsets and so cannot express a padding that differs by side. Its aspect is
    /// made to match the viewport's, which leaves the fit exact and <c>preserveAspectRatio</c> with
    /// nothing to do. With every side zero this is what <c>xMidYMid meet</c> already produces — the
    /// caller keeps the shorter path for that case rather than moving output nobody asked to pad.
    /// </remarks>
    private static SvgViewBox Frame(SKRect content, SKSize target, SvgPadding padding)
    {
        if (content.Width <= 0f || content.Height <= 0f)
        {
            throw new ArgumentException($"A drawing of {content.Width}x{content.Height} has no frame to pad.");
        }

        var across = target.Width * (1f - padding.Left - padding.Right);
        var down = target.Height * (1f - padding.Top - padding.Bottom);

        // One scale for both axes, which is what keeps the drawing's shape.
        var scale = Math.Min(across / content.Width, down / content.Height);

        // Centred in what the padding leaves, which is what the unpadded case does in the whole.
        var left = target.Width * padding.Left + (across - content.Width * scale) / 2f;
        var top = target.Height * padding.Top + (down - content.Height * scale) / 2f;

        return new SvgViewBox(
            content.Left - left / scale,
            content.Top - top / scale,
            target.Width / scale,
            target.Height / scale);
    }

    /// <summary>The frame the drawing's own coordinates are written in.</summary>
    /// <remarks>
    /// The viewBox where there is one, which is not the same as the natural size: a document 24 wide
    /// with a <c>0 0 48 48</c> viewBox measures 24 and draws in 48. Padding is placed against what
    /// the document declares and never against what it happens to ink, so slack an author left
    /// stays slack.
    /// </remarks>
    private static SKRect ContentFrame(SvgFragment fragment, SKRect natural)
        => HasViewBox(fragment)
            ? SKRect.Create(
                fragment.ViewBox.MinX,
                fragment.ViewBox.MinY,
                fragment.ViewBox.Width,
                fragment.ViewBox.Height)
            : natural;

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
