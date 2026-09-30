// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>The sections the element panel files an attribute under, in the order they are shown.</summary>
internal enum SvgViewerAttributeGroup
{
    General,
    Geometry,
    Link,
    Text,
    Fill,
    Stroke,
    Markers,
    Transform,
    Visibility,
    Effects,
    Units,
    Filter,
    Rendering,
    Other
}

/// <summary>What a row offers beside its box, where the attribute is one this element usually has.</summary>
internal enum SvgViewerAttributeControl
{
    None,
    Number,
    Fraction,
    Colour
}

/// <summary>What the element panel knows about one attribute beyond its name.</summary>
/// <param name="Choices">
/// Values in SVG's spelling. <c>#tag</c> stands for a <c>url(#id)</c> to each <c>&lt;tag&gt;</c> the
/// drawing gives an id.
/// </param>
/// <param name="Usual">The elements the row is listed on unasked, or null for none.</param>
internal sealed record SvgViewerAttribute(
    string Name,
    string Label,
    SvgViewerAttributeGroup Group,
    SvgViewerAttributeControl Control,
    IReadOnlyList<string> Choices,
    int Rank,
    IReadOnlySet<string>? Usual)
{
    public bool IsUsualOn(string element) => Usual is { } usual && (usual.Contains("*") || usual.Contains(element));
}

/// <summary>
/// Labels, sections and controls for the attributes the element panel lists.
/// </summary>
/// <remarks>
/// Kept apart from <c>SvgExpressionAttributes</c>, whose table the recipes, the element factory and
/// the diagnostics all read: it may only hold what an expression can drive, and this holds everything
/// a person might set.
/// </remarks>
internal static class SvgViewerAttributes
{
    private const string Shapes = "rect circle ellipse line polyline polygon path";
    private const string Texts = "text tspan textPath";
    private const string Holders = "g svg symbol a switch use";
    private const string Painted = Shapes + " " + Texts + " " + Holders;
    private const string Drawn = Painted + " image foreignObject";
    private const string Moved = Shapes + " " + Texts + " g use image a switch foreignObject";
    private const string Lines = "path line polyline polygon";
    private const string Gradients = "linearGradient radialGradient";
    private const string Paints = "none|currentColor|#linearGradient|#radialGradient|#pattern";
    private const string Units = "objectBoundingBox|userSpaceOnUse";
    private const string Inputs = "SourceGraphic|SourceAlpha|BackgroundImage|BackgroundAlpha|FillPaint|StrokePaint";

    private const SvgViewerAttributeControl Number = SvgViewerAttributeControl.Number;
    private const SvgViewerAttributeControl Fraction = SvgViewerAttributeControl.Fraction;
    private const SvgViewerAttributeControl Colour = SvgViewerAttributeControl.Colour;
    private const SvgViewerAttributeControl Plain = SvgViewerAttributeControl.None;

    /// <summary>Name, label (empty to spell it from the name), control, choices, usual on.</summary>
    private static readonly (SvgViewerAttributeGroup Group, (string, string, SvgViewerAttributeControl, string, string)[] Rows)[] s_table =
    {
        (SvgViewerAttributeGroup.General, new[]
        {
            ("id", "ID", Plain, "", "*"),
            ("class", "Class", Plain, "", ""),
            ("style", "Inline style", Plain, "", ""),
            ("systemLanguage", "Languages", Plain, "", ""),
            ("requiredFeatures", "", Plain, "", ""),
            ("requiredExtensions", "", Plain, "", ""),
        }),
        (SvgViewerAttributeGroup.Geometry, new[]
        {
            ("x", "X", Number, "", "rect image use svg foreignObject pattern text tspan"),
            ("y", "Y", Number, "", "rect image use svg foreignObject pattern text tspan"),
            ("width", "Width", Number, "", "rect image use svg foreignObject pattern"),
            ("height", "Height", Number, "", "rect image use svg foreignObject pattern"),
            ("cx", "Centre X", Number, "", "circle ellipse radialGradient"),
            ("cy", "Centre Y", Number, "", "circle ellipse radialGradient"),
            ("r", "Radius", Number, "", "circle radialGradient"),
            ("rx", "Radius X", Number, "", "rect ellipse"),
            ("ry", "Radius Y", Number, "", "rect ellipse"),
            ("x1", "Start X", Number, "", "line linearGradient"),
            ("y1", "Start Y", Number, "", "line linearGradient"),
            ("x2", "End X", Number, "", "line linearGradient"),
            ("y2", "End Y", Number, "", "line linearGradient"),
            ("fx", "Focus X", Number, "", "radialGradient"),
            ("fy", "Focus Y", Number, "", "radialGradient"),
            ("fr", "Focus radius", Number, "", "radialGradient"),
            ("points", "Points", Plain, "", "polyline polygon"),
            ("d", "Path data", Plain, "", "path"),
            ("dx", "Shift X", Number, "", "text tspan"),
            ("dy", "Shift Y", Number, "", "text tspan"),
            ("offset", "Offset", Fraction, "", "stop"),
            ("spreadMethod", "Spread", Plain, "pad|reflect|repeat", Gradients),
            ("viewBox", "View box", Plain, "", "svg symbol marker pattern"),
            ("preserveAspectRatio", "Aspect ratio", Plain, "xMidYMid meet|xMidYMid slice|xMinYMin meet|none", "svg image symbol marker pattern"),
            ("refX", "Reference X", Number, "", "marker"),
            ("refY", "Reference Y", Number, "", "marker"),
            ("markerWidth", "Marker width", Number, "", "marker"),
            ("markerHeight", "Marker height", Number, "", "marker"),
            ("orient", "Orientation", Plain, "auto|auto-start-reverse", "marker"),
            ("pathLength", "Path length", Number, "", ""),
        }),
        (SvgViewerAttributeGroup.Link, new[]
        {
            ("href", "Link", Plain, "", "use image textPath linearGradient radialGradient pattern a"),
            ("target", "Opens in", Plain, "_self|_blank|_parent|_top", "a"),
        }),
        (SvgViewerAttributeGroup.Text, new[]
        {
            ("font-family", "Font", Plain, "sans-serif|serif|monospace|cursive|fantasy", Texts),
            ("font-size", "Font size", Number, "", Texts),
            ("font-weight", "Font weight", Plain, "normal|bold|lighter|bolder|100|200|300|400|500|600|700|800|900", Texts),
            ("font-style", "Font style", Plain, "normal|italic|oblique", Texts),
            ("text-anchor", "Alignment", Plain, "start|middle|end", Texts),
            ("dominant-baseline", "Baseline", Plain, "auto|middle|central|hanging|alphabetic|ideographic|mathematical|text-before-edge|text-after-edge", Texts),
            ("letter-spacing", "Letter spacing", Number, "", Texts),
            ("word-spacing", "Word spacing", Number, "", Texts),
            ("text-decoration", "Decoration", Plain, "none|underline|overline|line-through", Texts),
            ("textLength", "Text length", Number, "", Texts),
            ("startOffset", "Start offset", Number, "", "textPath"),
            ("xml:space", "Keep spaces", Plain, "default|preserve", "text"),
            ("baseline-shift", "Baseline shift", Plain, "baseline|sub|super", "tspan"),
            ("lengthAdjust", "Length adjust", Plain, "spacing|spacingAndGlyphs", ""),
            ("rotate", "Glyph rotation", Plain, "", ""),
            ("method", "", Plain, "align|stretch", ""),
            ("spacing", "", Plain, "auto|exact", ""),
            ("side", "", Plain, "left|right", ""),
            ("font-variant", "", Plain, "normal|small-caps", ""),
            ("font-stretch", "", Plain, "", ""),
            ("font", "Font shorthand", Plain, "", ""),
            ("font-kerning", "Kerning", Plain, "auto|normal|none", ""),
            ("kerning", "", Plain, "", ""),
            ("font-size-adjust", "", Plain, "", ""),
            ("font-feature-settings", "", Plain, "", ""),
            ("font-variant-ligatures", "Ligatures", Plain, "", ""),
            ("writing-mode", "", Plain, "horizontal-tb|vertical-rl|vertical-lr", ""),
            ("direction", "", Plain, "ltr|rtl", ""),
            ("unicode-bidi", "Bidi", Plain, "", ""),
            ("alignment-baseline", "", Plain, "", ""),
            ("glyph-orientation-horizontal", "", Plain, "", ""),
            ("glyph-orientation-vertical", "", Plain, "", ""),
            ("white-space", "", Plain, "normal|pre|nowrap|pre-wrap|pre-line", ""),
            ("white-space-collapse", "", Plain, "", ""),
            ("white-space-trim", "", Plain, "", ""),
            ("text-transform", "Case", Plain, "none|capitalize|uppercase|lowercase", ""),
            ("text-overflow", "", Plain, "", ""),
            ("text-wrap-mode", "", Plain, "", ""),
            ("line-height", "", Plain, "", ""),
            ("line-break", "", Plain, "", ""),
            ("word-break", "", Plain, "", ""),
            ("overflow-wrap", "", Plain, "", ""),
            ("inline-size", "Wrap width", Plain, "", ""),
            ("shape-inside", "", Plain, "", ""),
            ("shape-subtract", "", Plain, "", ""),
            ("shape-padding", "", Plain, "", ""),
            ("shape-margin", "", Plain, "", ""),
            ("shape-image-threshold", "", Plain, "", ""),
        }),
        (SvgViewerAttributeGroup.Fill, new[]
        {
            ("fill", "Fill", Colour, Paints, Painted),
            ("fill-opacity", "Fill opacity", Fraction, "", Painted),
            ("fill-rule", "Fill rule", Plain, "nonzero|evenodd", "path polygon polyline"),
            ("stop-color", "Stop colour", Colour, "currentColor", "stop"),
            ("stop-opacity", "Stop opacity", Fraction, "", "stop"),
            ("flood-color", "Flood colour", Colour, "currentColor", "feFlood feDropShadow"),
            ("flood-opacity", "Flood opacity", Fraction, "", "feFlood feDropShadow"),
            ("lighting-color", "Lighting colour", Colour, "currentColor", "feDiffuseLighting feSpecularLighting"),
            ("color", "Current colour", Colour, "", ""),
        }),
        (SvgViewerAttributeGroup.Stroke, new[]
        {
            ("stroke", "Stroke", Colour, Paints, Painted),
            ("stroke-width", "Stroke width", Number, "", Painted),
            ("stroke-opacity", "Stroke opacity", Fraction, "", Painted),
            ("stroke-dasharray", "Dash pattern", Plain, "none", Shapes),
            ("stroke-dashoffset", "Dash offset", Number, "", Shapes),
            ("stroke-linecap", "Line cap", Plain, "butt|round|square", "path line polyline"),
            ("stroke-linejoin", "Line join", Plain, "miter|round|bevel", "rect path polyline polygon"),
            ("stroke-miterlimit", "Miter limit", Number, "", ""),
            ("vector-effect", "", Plain, "none|non-scaling-stroke", ""),
            ("paint-order", "", Plain, "normal|stroke|markers", ""),
        }),
        (SvgViewerAttributeGroup.Markers, new[]
        {
            ("marker-start", "Start marker", Plain, "none|#marker", Lines),
            ("marker-mid", "Middle markers", Plain, "none|#marker", Lines),
            ("marker-end", "End marker", Plain, "none|#marker", Lines),
        }),
        (SvgViewerAttributeGroup.Transform, new[]
        {
            ("transform", "Transform", Plain, "", Moved),
            ("gradientTransform", "", Plain, "", Gradients),
            ("patternTransform", "", Plain, "", "pattern"),
            ("transform-origin", "", Plain, "", ""),
            ("transform-box", "", Plain, "view-box|fill-box|stroke-box", ""),
        }),
        (SvgViewerAttributeGroup.Visibility, new[]
        {
            ("opacity", "Opacity", Fraction, "", Drawn),
            ("visibility", "Visibility", Plain, "visible|hidden|collapse", Drawn),
            ("display", "Display", Plain, "inline|none", Drawn),
            ("overflow", "Overflow", Plain, "visible|hidden|scroll|auto", ""),
        }),
        (SvgViewerAttributeGroup.Effects, new[]
        {
            ("filter", "Filter", Plain, "none|#filter", Drawn),
            ("clip-path", "Clip path", Plain, "none|#clipPath", Drawn),
            ("mask", "Mask", Plain, "none|#mask", Drawn),
            ("clip-rule", "Clip rule", Plain, "nonzero|evenodd", ""),
            ("mask-type", "", Plain, "luminance|alpha", ""),
            ("clip", "Clip rectangle", Plain, "", ""),
            ("enable-background", "Background", Plain, "", ""),
        }),
        (SvgViewerAttributeGroup.Units, new[]
        {
            ("gradientUnits", "", Plain, Units, Gradients),
            ("patternUnits", "", Plain, Units, "pattern"),
            ("patternContentUnits", "Content units", Plain, Units, "pattern"),
            ("clipPathUnits", "Clip units", Plain, Units, "clipPath"),
            ("maskUnits", "", Plain, Units, "mask"),
            ("maskContentUnits", "Content units", Plain, Units, "mask"),
            ("filterUnits", "", Plain, Units, "filter"),
            ("primitiveUnits", "", Plain, Units, "filter"),
            ("markerUnits", "", Plain, "strokeWidth|userSpaceOnUse", "marker"),
        }),
        (SvgViewerAttributeGroup.Filter, new[]
        {
            ("stdDeviation", "Blur", Number, "", "feGaussianBlur feDropShadow"),
            ("in", "Input", Plain, Inputs, ""),
            ("in2", "Second input", Plain, Inputs, ""),
            ("result", "", Plain, "", ""),
        }),
        (SvgViewerAttributeGroup.Rendering, new[]
        {
            ("shape-rendering", "", Plain, "auto|optimizeSpeed|crispEdges|geometricPrecision", ""),
            ("text-rendering", "", Plain, "auto|optimizeSpeed|optimizeLegibility|geometricPrecision", ""),
            ("image-rendering", "", Plain, "auto|optimizeSpeed|optimizeQuality", ""),
            ("color-rendering", "Colour rendering", Plain, "auto|optimizeSpeed|optimizeQuality", ""),
            ("color-interpolation", "Colour interpolation", Plain, "auto|sRGB|linearRGB", ""),
            ("color-interpolation-filters", "Filter interpolation", Plain, "auto|sRGB|linearRGB", ""),
            ("color-profile", "Colour profile", Plain, "", ""),
            ("pointer-events", "", Plain, "auto|none|visiblePainted|visibleFill|visibleStroke|visible|painted|fill|stroke|all", ""),
            ("cursor", "", Plain, "", ""),
        }),
    };

    private static readonly Dictionary<string, SvgViewerAttribute> s_known = Build();

    private static readonly Dictionary<string, string> s_titles = new(StringComparer.Ordinal)
    {
        ["svg"] = "Drawing",
        ["g"] = "Group",
        ["rect"] = "Rectangle",
        ["tspan"] = "Text span",
        ["textPath"] = "Text on a path",
        ["a"] = "Link",
        ["stop"] = "Gradient stop",
        ["linearGradient"] = "Linear gradient",
        ["radialGradient"] = "Radial gradient",
        ["clipPath"] = "Clip path",
        ["defs"] = "Definitions",
        ["desc"] = "Description",
        ["foreignObject"] = "Foreign object",
        ["feFlood"] = "Flood",
        ["feGaussianBlur"] = "Blur",
        ["feDropShadow"] = "Drop shadow",
        ["feColorMatrix"] = "Colour matrix",
    };

    /// <summary>What the panel knows about <paramref name="name"/>, or a row made up from the name alone.</summary>
    public static SvgViewerAttribute Find(string name)
    {
        // xlink:href is the same row as href: the prefix says how it is written, not what it is.
        var key = name.StartsWith("xlink:", StringComparison.Ordinal) ? name.Substring(6) : name;

        return s_known.TryGetValue(key, out var known)
            ? known
            : new SvgViewerAttribute(
                name,
                Spelt(name),
                SvgViewerAttributeGroup.Other,
                SvgViewerAttributeControl.None,
                Array.Empty<string>(),
                int.MaxValue,
                null);
    }

    /// <summary>The attributes listed on an element called <paramref name="element"/> whether written or not.</summary>
    public static IEnumerable<string> Usual(string element)
        => s_known.Values.Where(about => about.IsUsualOn(element)).OrderBy(about => about.Rank).Select(about => about.Name);

    /// <summary>What an element called <paramref name="element"/> is called by somebody reading it.</summary>
    public static string Title(string element)
        => s_titles.TryGetValue(element, out var title)
            ? title
            : element.Length > 2 && element.StartsWith("fe", StringComparison.Ordinal) && char.IsUpper(element[2])
                ? Spelt(element.Substring(2))
                : Spelt(element);

    /// <summary>A label from a name nobody wrote one for: <c>gradientUnits</c> reads "Gradient units".</summary>
    /// <remarks>A prefixed name is left alone, since the prefix is somebody else's vocabulary.</remarks>
    private static string Spelt(string name)
    {
        if (name.Contains(':') || name.Length == 0)
        {
            return name;
        }

        var spelt = new StringBuilder(name.Length + 4);

        foreach (var c in name)
        {
            if (c is '-' or '_')
            {
                spelt.Append(' ');
            }
            else if (char.IsUpper(c) && spelt.Length > 0 && spelt[spelt.Length - 1] != ' ')
            {
                spelt.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                spelt.Append(spelt.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }
        }

        return spelt.ToString();
    }

    private static Dictionary<string, SvgViewerAttribute> Build()
    {
        var known = new Dictionary<string, SvgViewerAttribute>(StringComparer.Ordinal);
        var rank = 0;

        foreach (var (group, rows) in s_table)
        {
            foreach (var (name, label, control, choices, usual) in rows)
            {
                known[name] = new SvgViewerAttribute(
                    name,
                    label.Length > 0 ? label : Spelt(name),
                    group,
                    control,
                    choices.Length > 0 ? choices.Split('|') : Array.Empty<string>(),
                    rank++,
                    usual.Length > 0 ? new HashSet<string>(usual.Split(' '), StringComparer.Ordinal) : null);
            }
        }

        return known;
    }
}
