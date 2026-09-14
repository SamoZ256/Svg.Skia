// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;

namespace Svg.PaintCode;

/// <summary>What a PaintCode document holds, with none of SVG's vocabulary in it.</summary>
/// <remarks>
/// Deliberately a mirror of the archived classes rather than something already half-converted, so the
/// reader can be tested against the file and the writer against the model, separately.
/// </remarks>
public sealed class PaintCodeDocument
{
    internal PaintCodeDocument(
        string name,
        IReadOnlyList<PaintCodeDesk> desks,
        IReadOnlyList<PaintCodeVariable> variables)
    {
        Name = name;
        Desks = desks;
        Variables = variables;
    }

    public string Name { get; }

    public IReadOnlyList<PaintCodeDesk> Desks { get; }

    /// <summary>The library's variables, in declaration order.</summary>
    public IReadOnlyList<PaintCodeVariable> Variables { get; }

    public IEnumerable<PaintCodeCanvas> Canvases
    {
        get
        {
            foreach (var desk in Desks)
            {
                foreach (var canvas in desk.Canvases)
                {
                    yield return canvas;
                }
            }
        }
    }

    public static PaintCodeDocument Load(string path) => PaintCodeReader.Load(path);

    public static PaintCodeDocument Parse(byte[] bytes) => PaintCodeReader.Parse(bytes);
}

public sealed class PaintCodeDesk
{
    internal PaintCodeDesk(string name, IReadOnlyList<PaintCodeCanvas> canvases)
    {
        Name = name;
        Canvases = canvases;
    }

    public string Name { get; }

    public IReadOnlyList<PaintCodeCanvas> Canvases { get; }
}

public sealed class PaintCodeCanvas
{
    internal PaintCodeCanvas(
        string name,
        string identifier,
        PaintCodeRect bounds,
        bool isExported,
        bool isAvailableAsSymbol,
        PaintCodeGroup root)
    {
        Name = name;
        Identifier = identifier;
        Bounds = bounds;
        IsExported = isExported;
        IsAvailableAsSymbol = isAvailableAsSymbol;
        Root = root;
    }

    public string Name { get; }

    /// <summary>The name a <see cref="PaintCodeSymbolItem"/> refers to it by.</summary>
    public string Identifier { get; }

    public PaintCodeRect Bounds { get; }

    public bool IsExported { get; }

    public bool IsAvailableAsSymbol { get; }

    public PaintCodeGroup Root { get; }
}

public enum PaintCodeShapeKind
{
    Bezier,
    Rectangle,
    RoundedRectangle,
    Oval,
    Star,
    Polygon
}

/// <summary>The frame and the bindings every drawable item carries.</summary>
public abstract class PaintCodeItem
{
    private protected PaintCodeItem(string name, PaintCodeFrame frame, IReadOnlyDictionary<string, PaintCodeBinding> bindings)
    {
        Name = name;
        Frame = frame;
        Bindings = bindings;
    }

    public string Name { get; }

    public PaintCodeFrame Frame { get; }

    /// <summary>Expressions driving this item's properties, keyed by PaintCode's own property name.</summary>
    public IReadOnlyDictionary<string, PaintCodeBinding> Bindings { get; }
}

/// <summary>Position, size, anchor and transform, as PaintCode stores them.</summary>
/// <remarks>
/// <see cref="Anchor"/> is the item's position in canvas space and the origin its path points are
/// written relative to; y is up, so a converted point is <c>(p.X + Anchor.X, -(p.Y + Anchor.Y))</c>.
/// </remarks>
public sealed class PaintCodeFrame
{
    internal PaintCodeFrame(
        double x,
        double y,
        double width,
        double height,
        PaintCodePoint anchor,
        double rotation,
        double scaleX,
        double scaleY,
        double alpha,
        bool isHidden,
        bool isVisible)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Anchor = anchor;
        Rotation = rotation;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Alpha = alpha;
        IsHidden = isHidden;
        IsVisible = isVisible;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public PaintCodePoint Anchor { get; }

    public double Rotation { get; }

    public double ScaleX { get; }

    public double ScaleY { get; }

    public double Alpha { get; }

    public bool IsHidden { get; }

    /// <summary>PaintCode's <c>visibilityMode</c>, which is the value its expressions drive.</summary>
    public bool IsVisible { get; }
}

public sealed class PaintCodeGroup : PaintCodeItem
{
    internal PaintCodeGroup(
        string name,
        PaintCodeFrame frame,
        IReadOnlyDictionary<string, PaintCodeBinding> bindings,
        IReadOnlyList<PaintCodeItem> children,
        PaintCodeShape? clip)
        : base(name, frame, bindings)
    {
        Children = children;
        Clip = clip;
    }

    public IReadOnlyList<PaintCodeItem> Children { get; }

    /// <summary>The shape this group clips to, where it has one.</summary>
    public PaintCodeShape? Clip { get; }
}

public sealed class PaintCodeShape : PaintCodeItem
{
    internal PaintCodeShape(
        string name,
        PaintCodeShapeKind kind,
        PaintCodeFrame frame,
        IReadOnlyDictionary<string, PaintCodeBinding> bindings,
        PaintCodePath? path,
        PaintCodePaint fill,
        PaintCodePaint stroke,
        PaintCodeStroke strokeStyle,
        bool isEvenOdd,
        PaintCodeText? text,
        PaintCodeShapeMetrics metrics)
        : base(name, frame, bindings)
    {
        Kind = kind;
        Path = path;
        Fill = fill;
        Stroke = stroke;
        StrokeStyle = strokeStyle;
        IsEvenOdd = isEvenOdd;
        Text = text;
        Metrics = metrics;
    }

    public PaintCodeShapeKind Kind { get; }

    /// <summary>The contours of a <see cref="PaintCodeShapeKind.Bezier"/>; null for the rest.</summary>
    public PaintCodePath? Path { get; }

    public PaintCodePaint Fill { get; }

    public PaintCodePaint Stroke { get; }

    public PaintCodeStroke StrokeStyle { get; }

    public bool IsEvenOdd { get; }

    public PaintCodeText? Text { get; }

    /// <summary>The numbers the shape's own kind needs: corner radius, angles, sides.</summary>
    public PaintCodeShapeMetrics Metrics { get; }
}

public sealed class PaintCodeSymbolItem : PaintCodeItem
{
    internal PaintCodeSymbolItem(
        string name,
        PaintCodeFrame frame,
        IReadOnlyDictionary<string, PaintCodeBinding> bindings,
        string targetIdentifier,
        string targetName)
        : base(name, frame, bindings)
    {
        TargetIdentifier = targetIdentifier;
        TargetName = targetName;
    }

    public string TargetIdentifier { get; }

    public string TargetName { get; }
}

public sealed class PaintCodePath
{
    internal PaintCodePath(IReadOnlyList<PaintCodeContour> contours)
    {
        Contours = contours;
    }

    public IReadOnlyList<PaintCodeContour> Contours { get; }
}

public sealed class PaintCodeContour
{
    internal PaintCodeContour(IReadOnlyList<PaintCodePathPoint> points, bool isClosed)
    {
        Points = points;
        IsClosed = isClosed;
    }

    public IReadOnlyList<PaintCodePathPoint> Points { get; }

    public bool IsClosed { get; }
}

/// <summary>A point and its two control points, which are offsets from the point itself.</summary>
public sealed class PaintCodePathPoint
{
    internal PaintCodePathPoint(PaintCodePoint position, PaintCodePoint entering, PaintCodePoint exiting)
    {
        Position = position;
        Entering = entering;
        Exiting = exiting;
    }

    public PaintCodePoint Position { get; }

    public PaintCodePoint Entering { get; }

    public PaintCodePoint Exiting { get; }
}

public enum PaintCodePaintKind
{
    None,
    Color,
    Gradient
}

public sealed class PaintCodePaint
{
    internal static readonly PaintCodePaint None = new(PaintCodePaintKind.None, null, null);

    internal PaintCodePaint(PaintCodePaintKind kind, PaintCodeColor? color, PaintCodeGradient? gradient)
    {
        Kind = kind;
        Color = color;
        Gradient = gradient;
    }

    public PaintCodePaintKind Kind { get; }

    public PaintCodeColor? Color { get; }

    public PaintCodeGradient? Gradient { get; }
}

/// <summary>A colour, resolved to the sRGB bytes PaintCode itself emits.</summary>
public sealed class PaintCodeColor
{
    internal PaintCodeColor(string name, byte red, byte green, byte blue, double alpha, bool isApproximate = false)
    {
        Name = name;
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        IsApproximate = isApproximate;
    }

    public string Name { get; }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public double Alpha { get; }

    /// <summary>
    /// Set where the colour derives from another by an operation this reader has no equivalent for, so
    /// the parent's own colour stands in. The conversion reports it rather than passing it off.
    /// </summary>
    public bool IsApproximate { get; }
}

public sealed class PaintCodeGradient
{
    internal PaintCodeGradient(string name, IReadOnlyList<PaintCodeGradientStop> stops)
    {
        Name = name;
        Stops = stops;
    }

    public string Name { get; }

    public IReadOnlyList<PaintCodeGradientStop> Stops { get; }
}

public sealed class PaintCodeGradientStop
{
    internal PaintCodeGradientStop(PaintCodeColor color, double location, double interRatio, bool isMiddle)
    {
        Color = color;
        Location = location;
        InterRatio = interRatio;
        IsMiddle = isMiddle;
    }

    public PaintCodeColor Color { get; }

    public double Location { get; }

    /// <summary>Where the blend's midpoint sits between this stop and the next, which SVG has no word for.</summary>
    public double InterRatio { get; }

    public bool IsMiddle { get; }
}

public sealed class PaintCodeStroke
{
    internal static readonly PaintCodeStroke None = new(0, 0, 0, 10, false, 0, 0, 0);

    internal PaintCodeStroke(
        double width,
        int cap,
        int join,
        double miterLimit,
        bool hasPattern,
        double dash,
        double gap,
        double phase)
    {
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
        HasPattern = hasPattern;
        Dash = dash;
        Gap = gap;
        Phase = phase;
    }

    public double Width { get; }

    public int Cap { get; }

    public int Join { get; }

    public double MiterLimit { get; }

    public bool HasPattern { get; }

    public double Dash { get; }

    public double Gap { get; }

    public double Phase { get; }
}

public sealed class PaintCodeText
{
    internal PaintCodeText(
        string value,
        string fontFamily,
        string fontFace,
        double fontSize,
        PaintCodeColor? color,
        int horizontalAlignment,
        int verticalAlignment)
    {
        Value = value;
        FontFamily = fontFamily;
        FontFace = fontFace;
        FontSize = fontSize;
        Color = color;
        HorizontalAlignment = horizontalAlignment;
        VerticalAlignment = verticalAlignment;
    }

    public string Value { get; }

    public string FontFamily { get; }

    public string FontFace { get; }

    public double FontSize { get; }

    public PaintCodeColor? Color { get; }

    public int HorizontalAlignment { get; }

    public int VerticalAlignment { get; }
}

/// <summary>The per-kind numbers: a corner radius, an oval's sweep, a star's or polygon's sides.</summary>
public sealed class PaintCodeShapeMetrics
{
    internal static readonly PaintCodeShapeMetrics Default = new(0, true, true, true, true, 0, 360, true, 0, 0);

    internal PaintCodeShapeMetrics(
        double cornerRadius,
        bool topLeftRounded,
        bool topRightRounded,
        bool bottomLeftRounded,
        bool bottomRightRounded,
        double startAngle,
        double endAngle,
        bool isClosed,
        int sides,
        double innerRadiusPercentage)
    {
        CornerRadius = cornerRadius;
        TopLeftRounded = topLeftRounded;
        TopRightRounded = topRightRounded;
        BottomLeftRounded = bottomLeftRounded;
        BottomRightRounded = bottomRightRounded;
        StartAngle = startAngle;
        EndAngle = endAngle;
        IsClosed = isClosed;
        Sides = sides;
        InnerRadiusPercentage = innerRadiusPercentage;
    }

    public double CornerRadius { get; }

    public bool TopLeftRounded { get; }

    public bool TopRightRounded { get; }

    public bool BottomLeftRounded { get; }

    public bool BottomRightRounded { get; }

    public double StartAngle { get; }

    public double EndAngle { get; }

    public bool IsClosed { get; }

    public int Sides { get; }

    public double InnerRadiusPercentage { get; }
}

public enum PaintCodeValueKind
{
    Number,
    String,
    Boolean,
    Color,
    Gradient,
    Rect,
    Other
}

/// <summary>A library variable: an input the drawing takes, or an expression derived from them.</summary>
public sealed class PaintCodeVariable
{
    internal PaintCodeVariable(
        string name,
        PaintCodeValueKind kind,
        string? expression,
        PaintCodeBinding value,
        double? minimum,
        double? maximum)
    {
        Name = name;
        Kind = kind;
        Expression = expression;
        Value = value;
        Minimum = minimum;
        Maximum = maximum;
    }

    public string Name { get; }

    public PaintCodeValueKind Kind { get; }

    /// <summary>The expression deriving it, or null where it is an input.</summary>
    public string? Expression { get; }

    /// <summary>Its value as the document was saved, which becomes the parameter's default.</summary>
    public PaintCodeBinding Value { get; }

    public double? Minimum { get; }

    public double? Maximum { get; }

    public bool IsDerived => Expression is { };
}

/// <summary>An expression driving a property, together with the value it had when the file was saved.</summary>
public sealed class PaintCodeBinding
{
    internal PaintCodeBinding(
        string? expression,
        PaintCodeValueKind kind,
        double? number,
        bool? flag,
        string? text,
        PaintCodeColor? color,
        PaintCodeGradient? gradient,
        PaintCodeRect? rect)
    {
        Expression = expression;
        Kind = kind;
        Number = number;
        Flag = flag;
        Text = text;
        Color = color;
        Gradient = gradient;
        Rect = rect;
    }

    public string? Expression { get; }

    public PaintCodeValueKind Kind { get; }

    public double? Number { get; }

    public bool? Flag { get; }

    public string? Text { get; }

    public PaintCodeColor? Color { get; }

    public PaintCodeGradient? Gradient { get; }

    public PaintCodeRect? Rect { get; }
}
