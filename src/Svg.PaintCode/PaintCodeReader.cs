// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Svg.PaintCode;

/// <summary>Turns a decoded keyed archive into the <see cref="PaintCodeDocument"/> model.</summary>
internal static class PaintCodeReader
{
    // PaintCode's own type tags on a value provider.
    private const int TypeNumber = 2;
    private const int TypeString = 3;
    private const int TypeBoolean = 4;
    private const int TypeColor = 5;
    private const int TypeGradient = 6;
    private const int TypeRect = 10;

    // A variable whose value comes from an expression rather than from the caller.
    private const int KindDerived = 13;

    // PPColor.operation, for a colour derived from another. Read off PaintCode's own generated code,
    // which writes the chain out: accentColorGray is colorByChangingSaturation(accentColorOn, 0.2),
    // accentColorGrayShadow is colorByApplyingShadow of that, and accentColorOff an alpha over it.
    // Those three are the only operations the sample's 29 derived colours use.
    private const int OperationShadow = 1;
    private const int OperationAlpha = 2;
    private const int OperationSaturation = 3;

    internal static PaintCodeDocument Load(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return Parse(File.ReadAllBytes(path));
    }

    internal static PaintCodeDocument Parse(byte[] bytes)
    {
        var archive = PaintCodeKeyedArchive.Parse(bytes);
        var top = archive.Top;
        var name = top["styleKitName"].Text ?? top["projectName"].Text ?? "PaintCode";
        var desks = new List<PaintCodeDesk>();

        foreach (var desk in top["desks"].Items)
        {
            desks.Add(Desk(desk));
        }

        var library = top["library"];

        return new PaintCodeDocument(name, desks, Variables(library), Colors(library), Gradients(library));
    }

    private static PaintCodeDesk Desk(PaintCodeNode node)
    {
        var canvases = new List<PaintCodeCanvas>();

        foreach (var canvas in node["canvases"].Items)
        {
            canvases.Add(Canvas(canvas));
        }

        return new PaintCodeDesk(node["name"].Text ?? "Desk", canvases);
    }

    private static PaintCodeCanvas Canvas(PaintCodeNode node)
    {
        var name = node["name"].Text ?? "Canvas";

        return new PaintCodeCanvas(
            name,
            Identifier(name),
            node["bounds"].Rect ?? new PaintCodeRect(0, 0, 0, 0),
            node["isExported"].FlagOr(true),
            node["isAvailableAsSymbol"].FlagOr(false),
            Group(node["rootGroup"]));
    }

    /// <summary>The name with everything but letters and digits removed, which is how PaintCode keys a symbol.</summary>
    internal static string Identifier(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static PaintCodeItem Item(PaintCodeNode node)
        => node.ClassName switch
        {
            "PPGroup" => Group(node),
            "PPSymbol" => Symbol(node),
            "PPBezier" => Shape(node, PaintCodeShapeKind.Bezier),
            "PPRectangle" => Shape(node, PaintCodeShapeKind.Rectangle),
            "PPRoundedRectangle" => Shape(node, PaintCodeShapeKind.RoundedRectangle),
            "PPOval" => Shape(node, PaintCodeShapeKind.Oval),
            "PPStar" => Shape(node, PaintCodeShapeKind.Star),
            "PPPolygon" => Shape(node, PaintCodeShapeKind.Polygon),
            var other => throw new PaintCodeException($"The document holds a {other ?? "nameless"} item, which this reader has no case for.")
        };

    private static PaintCodeGroup Group(PaintCodeNode node)
    {
        var clip = node["clip"];
        var clips = !clip.IsNull && clip.ClassName is not ("PPNullItem" or null);
        var children = new List<PaintCodeItem>();

        foreach (var child in node["shapesAndGroups"].Items)
        {
            // A clipping group names one of its own children as the clip, by reference, and PaintCode
            // builds that shape's path but never paints it. Leaving it in the children would draw it.
            if (clips && child.SameAs(clip))
            {
                continue;
            }

            children.Add(Item(child));
        }

        return new PaintCodeGroup(
            node["name"].Text ?? "Group",
            Frame(node),
            Bindings(node),
            children,
            clips ? (PaintCodeShape)Item(clip) : null);
    }

    private static PaintCodeSymbolItem Symbol(PaintCodeNode node)
    {
        var provider = node["symbolProviderID"];

        return new PaintCodeSymbolItem(
            node["name"].Text ?? "Symbol",
            Frame(node),
            Bindings(node),
            provider["identifier"].Text ?? string.Empty,
            provider["name"].Text ?? string.Empty,
            Values(node["virtualProperties"]));
    }

    private static PaintCodeShape Shape(PaintCodeNode node, PaintCodeShapeKind kind)
    {
        var text = node["text"].Text ?? string.Empty;

        return new PaintCodeShape(
            node["name"].Text ?? kind.ToString(),
            kind,
            Frame(node),
            Bindings(node),
            kind is PaintCodeShapeKind.Bezier ? Path(node["path"]) : null,
            Paint(node["fill"]),
            Paint(node["strokeColor"]),
            Stroke(node),
            (int)node["windingRule"].NumberOr(0) == 1,
            text.Length == 0 ? null : Text(node, text),
            Metrics(node, kind),
            node["isFillGradientRadial"].FlagOr(false),
            node["fillGradientAngle"].NumberOr(-90),
            Ends(node),
            (int)node["blendMode"].NumberOr(0));
    }

    // PPShape.fillGradientType, where a gradient laid by dragging its two ends is 2. The angle is
    // still written beside it and is still the default nobody turned, so reading that instead drew
    // every one of these top to bottom.
    private const int GradientBetweenEnds = 2;

    /// <summary>The two handles a dragged gradient was laid by, or null where it was laid by angle.</summary>
    private static (PaintCodePoint Start, PaintCodePoint End)? Ends(PaintCodeNode node)
        => (int)node["fillGradientType"].NumberOr(0) == GradientBetweenEnds &&
           node["fillGradientStartCenter"].Point is { } start &&
           node["fillGradientEndCenter"].Point is { } end
            ? (start, end)
            : null;

    private static PaintCodeFrame Frame(PaintCodeNode node)
        => new(
            node["x"].NumberOr(0),
            node["y"].NumberOr(0),
            node["width"].NumberOr(0),
            node["height"].NumberOr(0),
            new PaintCodePoint(node["anchorX"].NumberOr(0), node["anchorY"].NumberOr(0)),
            node["rotation"].NumberOr(0),
            node["scaleX"].NumberOr(1),
            node["scaleY"].NumberOr(1),
            node["alpha"].NumberOr(1),
            node["isHidden"].FlagOr(false),
            (int)node["visibilityMode"].NumberOr(1) != 0);

    private static PaintCodePath? Path(PaintCodeNode node)
    {
        if (node.IsNull)
        {
            return null;
        }

        var closed = node["contoursClosedStatus"].Items;
        var contours = new List<PaintCodeContour>();
        var index = 0;

        foreach (var contour in node["contours"].Items)
        {
            var points = new List<PaintCodePathPoint>();

            foreach (var point in contour.Items)
            {
                points.Add(new PaintCodePathPoint(
                    point["position"].Point ?? default,
                    point["enteringControlPoint"].Point ?? default,
                    point["exitingControlPoint"].Point ?? default));
            }

            contours.Add(new PaintCodeContour(points, index < closed.Count && closed[index].FlagOr(false)));
            index++;
        }

        return new PaintCodePath(contours);
    }

    private static PaintCodePaint Paint(PaintCodeNode node)
        => node.ClassName switch
        {
            "PPColor" => new PaintCodePaint(PaintCodePaintKind.Color, Color(node), null),
            "PPGradient" => new PaintCodePaint(PaintCodePaintKind.Gradient, null, Gradient(node)),
            _ => PaintCodePaint.None
        };

    private static PaintCodeStroke Stroke(PaintCodeNode node)
        => new(
            node["strokeWidth"].NumberOr(0),
            (int)node["lineCapStyle"].NumberOr(0),
            (int)node["lineJoinStyle"].NumberOr(0),
            node["miterLimit"].NumberOr(10),
            node["hasStrokePattern"].FlagOr(false),
            node["strokePatternDash"].NumberOr(0),
            node["strokePatternGap"].NumberOr(0),
            node["strokePatternPhase"].NumberOr(0));

    private static PaintCodeText Text(PaintCodeNode node, string value)
    {
        var font = node["font"];

        return new PaintCodeText(
            value,
            font["familyName"].Text ?? font["fontName"].Text ?? "sans-serif",
            font["faceName"].Text ?? "Regular",
            node["fontSize"].NumberOr(12),
            node["fontColor"].ClassName is "PPColor" ? Color(node["fontColor"]) : null,
            (int)node["horizontalAlignment"].NumberOr(0),
            (int)node["verticalAlignment"].NumberOr(0),
            node["textInsetHorizontal"].NumberOr(0),
            node["textInsetVertical"].NumberOr(0));
    }

    private static PaintCodeShapeMetrics Metrics(PaintCodeNode node, PaintCodeShapeKind kind)
        => kind switch
        {
            PaintCodeShapeKind.Rectangle or PaintCodeShapeKind.RoundedRectangle => new PaintCodeShapeMetrics(
                node["cornerRadius"].NumberOr(0),
                node["isTopLeftCornerRounded"].FlagOr(true),
                node["isTopRightCornerRounded"].FlagOr(true),
                node["isBottomLeftCornerRounded"].FlagOr(true),
                node["isBottomRightCornerRounded"].FlagOr(true),
                0, 360, true, 0, 0),
            PaintCodeShapeKind.Oval => new PaintCodeShapeMetrics(
                0, true, true, true, true,
                node["startAngle"].NumberOr(0),
                node["endAngle"].NumberOr(0),
                node["isClosed"].FlagOr(true),
                0, 0),
            PaintCodeShapeKind.Star or PaintCodeShapeKind.Polygon => new PaintCodeShapeMetrics(
                0, true, true, true, true, 0, 360, true,
                (int)node["numberOfSides"].NumberOr(3),
                node["innerRadiusPercentage"].NumberOr(50)),
            _ => PaintCodeShapeMetrics.Default
        };

    private static PaintCodeColor? Color(PaintCodeNode node)
    {
        if (node.ClassName is not "PPColor")
        {
            return null;
        }

        var name = node["name"].Text ?? string.Empty;

        // A colour that is not derived carries its own components; the operation beside them is the
        // editor's record of how it was picked and has already been applied. A derived one instead
        // holds a sentinel there and means "the parent, changed by this operation".
        if (!node["isDerived"].FlagOr(false))
        {
            return Components(node["basicNSColor"], name) ?? new PaintCodeColor(name, 0, 0, 0, 1);
        }

        var parent = Color(node["parentColor"]);

        if (parent is null)
        {
            return Components(node["basicNSColor"], name) ?? new PaintCodeColor(name, 0, 0, 0, 1, true);
        }

        var operation = (int)node["operation"].NumberOr(0);
        var amount = node["operationAmount"].NumberOr(1);

        return operation switch
        {
            OperationAlpha => new PaintCodeColor(name, parent.Red, parent.Green, parent.Blue, amount, parent.IsApproximate),
            OperationShadow => Named(PaintCodeColorOperations.Shadow(parent, amount), name),
            OperationSaturation => Named(PaintCodeColorOperations.Saturation(parent, amount), name),

            // Anything else keeps the parent's own colour and says so, rather than guessing at a
            // shade: a colour that is nearly right is the one mistake nobody looks for.
            _ => new PaintCodeColor(name, parent.Red, parent.Green, parent.Blue, parent.Alpha, true)
        };
    }

    private static PaintCodeColor Named(PaintCodeColor colour, string name)
        => new(name, colour.Red, colour.Green, colour.Blue, colour.Alpha, colour.IsApproximate);

    // NSComponents holds the colour in its own colour space as ASCII floats. Every drawing colour in
    // the sample document is space 1 (sRGB), where those floats are exactly the bytes PaintCode's own
    // generated code emits; the 946 colours in space 3 are canvas grid chrome, which nothing reads.
    private static PaintCodeColor? Components(PaintCodeNode node, string name)
    {
        if (node["NSComponents"].Data is not { } data)
        {
            return null;
        }

        var space = (int)node["NSColorSpace"].NumberOr(0);
        var text = Encoding.ASCII.GetString(data).TrimEnd('\0').Trim();
        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new double[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return null;
            }
        }

        return numbers.Length switch
        {
            >= 4 when space == 1 => new PaintCodeColor(name, Byte(numbers[0]), Byte(numbers[1]), Byte(numbers[2]), numbers[3]),
            >= 2 => new PaintCodeColor(name, Byte(numbers[0]), Byte(numbers[0]), Byte(numbers[0]), numbers[1], true),
            _ => null
        };
    }

    private static byte Byte(double channel)
        => (byte)Math.Max(0, Math.Min(255, Math.Round(channel * 255, MidpointRounding.AwayFromZero)));

    private static PaintCodeGradient? Gradient(PaintCodeNode node)
    {
        if (node.ClassName is not "PPGradient")
        {
            return null;
        }

        var stops = new List<PaintCodeGradientStop>();

        foreach (var step in node["colorSteps"].Items)
        {
            if (Color(step["color"]) is not { } color)
            {
                continue;
            }

            stops.Add(new PaintCodeGradientStop(
                color,
                step["location"].NumberOr(0),
                step["interRatio"].NumberOr(0.5),
                step["isMiddleColor"].FlagOr(false)));
        }

        return new PaintCodeGradient(node["name"].Text ?? string.Empty, stops);
    }

    private static IReadOnlyDictionary<string, PaintCodeBinding> Bindings(PaintCodeNode node)
    {
        var providers = node["propertyValueProviders"].Entries;

        if (providers.Count == 0)
        {
            return EmptyBindings;
        }

        var bindings = new Dictionary<string, PaintCodeBinding>(providers.Count, StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            bindings[provider.Key] = Binding(provider.Value);
        }

        return bindings;
    }

    private static readonly Dictionary<string, PaintCodeBinding> EmptyBindings = new(StringComparer.Ordinal);

    /// <summary>
    /// The values a symbol instance hands its target, whose kinds are read from the values
    /// themselves: unlike a value provider, nothing here says what type it is.
    /// </summary>
    private static IReadOnlyDictionary<string, PaintCodeBinding> Values(PaintCodeNode node)
    {
        var entries = node.Entries;

        if (entries.Count == 0)
        {
            return EmptyBindings;
        }

        var values = new Dictionary<string, PaintCodeBinding>(entries.Count, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var value = entry.Value;

            if (value.ClassName is "PPColor" && Color(value) is { } color)
            {
                values[entry.Key] = new PaintCodeBinding(null, PaintCodeValueKind.Color, null, null, null, color, null, null);
            }
            else if (value.ClassName is "PPGradient" && Gradient(value) is { } gradient)
            {
                values[entry.Key] = new PaintCodeBinding(null, PaintCodeValueKind.Gradient, null, null, null, null, gradient, null);
            }
            else if (value.IsBoolean && value.Flag is { } flag)
            {
                values[entry.Key] = new PaintCodeBinding(null, PaintCodeValueKind.Boolean, null, flag, null, null, null, null);
            }
            else if (value.Number is { } number)
            {
                values[entry.Key] = new PaintCodeBinding(null, PaintCodeValueKind.Number, number, null, null, null, null, null);
            }
            else if (value.Text is { } text)
            {
                values[entry.Key] = new PaintCodeBinding(null, PaintCodeValueKind.String, null, null, text, null, null, null);
            }
        }

        return values;
    }

    private static PaintCodeBinding Binding(PaintCodeNode node)
    {
        var type = (int)node["type"].NumberOr(TypeNumber);
        var value = node["value"];
        var kind = type switch
        {
            TypeNumber => PaintCodeValueKind.Number,
            TypeString => PaintCodeValueKind.String,
            TypeBoolean => PaintCodeValueKind.Boolean,
            TypeColor => PaintCodeValueKind.Color,
            TypeGradient => PaintCodeValueKind.Gradient,
            TypeRect => PaintCodeValueKind.Rect,
            _ => PaintCodeValueKind.Other
        };

        return new PaintCodeBinding(
            node["expression"].Text,
            kind,
            kind is PaintCodeValueKind.Number ? value.Number : null,
            kind is PaintCodeValueKind.Boolean ? value.Flag : null,
            kind is PaintCodeValueKind.String ? value.Text : null,
            kind is PaintCodeValueKind.Color ? Color(value) : null,
            kind is PaintCodeValueKind.Gradient ? Gradient(value) : null,
            kind is PaintCodeValueKind.Rect ? value.Rect : null);
    }

    private static IReadOnlyList<PaintCodeLibraryColor> Colors(PaintCodeNode library)
    {
        var colors = new List<PaintCodeLibraryColor>();

        foreach (var color in library["colors"].Items)
        {
            if (Color(color) is not { } value)
            {
                continue;
            }

            var derived = color["isDerived"].FlagOr(false);
            var parent = derived ? color["parentColor"]["name"].Text : null;
            var alpha = derived && (int)color["operation"].NumberOr(0) == OperationAlpha
                ? color["operationAmount"].Number
                : null;

            // usage 1 is what PaintCode itself takes as a parameter of the drawing: the five colours
            // marked it in the sample are exactly the five its generated methods ask for.
            colors.Add(new PaintCodeLibraryColor(value.Name, value, (int)color["usage"].NumberOr(0) == 1, parent, alpha));
        }

        return colors;
    }

    private static IReadOnlyList<PaintCodeGradient> Gradients(PaintCodeNode library)
    {
        var gradients = new List<PaintCodeGradient>();

        foreach (var gradient in library["gradients"].Items)
        {
            if (Gradient(gradient) is { } value)
            {
                gradients.Add(value);
            }
        }

        return gradients;
    }

    private static IReadOnlyList<PaintCodeVariable> Variables(PaintCodeNode library)
    {
        var variables = new List<PaintCodeVariable>();

        foreach (var variable in library["variables"].Items)
        {
            var provider = variable["valueProvider"];
            var limit = provider["limit"];
            var bounded = limit.ClassName is "PPLimitInterval";

            variables.Add(new PaintCodeVariable(
                variable["name"].Text ?? string.Empty,
                Binding(provider).Kind,
                (int)variable["kind"].NumberOr(0) == KindDerived ? provider["expression"].Text : null,
                Binding(provider),
                (int)variable["usage"].NumberOr(0) == 1,
                bounded ? limit["min"].Number : null,
                bounded ? limit["max"].Number : null));
        }

        return variables;
    }
}
