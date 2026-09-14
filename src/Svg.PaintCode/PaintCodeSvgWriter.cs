// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Svg.PaintCode;

/// <summary>Writes one PaintCode canvas as one SVG document.</summary>
internal sealed class PaintCodeSvgWriter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly PaintCodeCanvas _canvas;
    private readonly ICollection<PaintCodeImportNote> _notes;
    private readonly Dictionary<string, int> _identifiers = new(StringComparer.Ordinal);
    private readonly List<XElement> _definitions = new();

    private PaintCodeSvgWriter(PaintCodeCanvas canvas, ICollection<PaintCodeImportNote> notes)
    {
        _canvas = canvas;
        _notes = notes;
    }

    internal static XDocument Write(PaintCodeCanvas canvas, ICollection<PaintCodeImportNote> notes)
        => new PaintCodeSvgWriter(canvas, notes).Document();

    private XDocument Document()
    {
        var bounds = _canvas.Bounds;
        var width = PaintCodePathData.Number(bounds.Width);
        var height = PaintCodePathData.Number(bounds.Height);
        var root = new XElement(
            Svg + "svg",
            new XAttribute("xmlns", Svg.NamespaceName),
            new XAttribute("width", width),
            new XAttribute("height", height),
            new XAttribute("viewBox", $"0 0 {width} {height}"));

        // A canvas's bounds place it on the desk; its contents are already measured from its own
        // top-left corner, so nothing here offsets them by that origin.
        var children = new List<XElement>();

        foreach (var child in _canvas.Root.Children)
        {
            if (Item(child) is { } element)
            {
                children.Add(element);
            }
        }

        if (_definitions.Count > 0)
        {
            root.Add(new XElement(Svg + "defs", _definitions));
        }

        root.Add(children);

        return new XDocument(root);
    }

    private XElement? Item(PaintCodeItem item)
        => item switch
        {
            PaintCodeGroup group => Group(group),
            PaintCodeShape shape => Shape(shape),
            PaintCodeSymbolItem symbol => Symbol(symbol),
            _ => null
        };

    private XElement? Group(PaintCodeGroup group)
    {
        var children = new List<XElement>();

        foreach (var child in group.Children)
        {
            if (Item(child) is { } converted)
            {
                children.Add(converted);
            }
        }

        if (children.Count == 0)
        {
            return null;
        }

        var element = new XElement(Svg + "g", children);
        element.SetAttributeValue("id", Identifier(group.Name));

        if (group.Clip is { } clip)
        {
            var identifier = Identifier(clip.Name + "-clip");
            var path = new XElement(Svg + "path");
            Geometry(path, clip);
            _definitions.Add(new XElement(Svg + "clipPath", new XAttribute("id", identifier), path));
            element.SetAttributeValue("clip-path", $"url(#{identifier})");
        }

        // A group's own anchor is part of the geometry only when it has something to turn or scale
        // around: PaintCode stores the children of an untransformed group in the space above it.
        Frame(element, group.Frame, forGroup: true);

        return element;
    }

    private XElement Shape(PaintCodeShape shape)
    {
        var element = Element(shape);
        element.SetAttributeValue("id", Identifier(shape.Name));
        Geometry(element, shape);
        Fill(element, shape);
        Stroke(element, shape);
        Frame(element, shape.Frame, forGroup: false);

        if (shape.Text is { } text)
        {
            Note(PaintCodeImportSeverity.Dropped, shape.Name, "text", $"'{text.Value}' is not written yet.");
        }

        return element;
    }

    private XElement? Symbol(PaintCodeSymbolItem symbol)
    {
        Note(PaintCodeImportSeverity.Dropped, symbol.Name, "symbol", $"'{symbol.TargetName}' is not written yet.");

        return null;
    }

    private XElement Element(PaintCodeShape shape)
    {
        if (PaintCodePathData.IsPlainRectangle(shape))
        {
            return new XElement(Svg + "rect");
        }

        return PaintCodePathData.IsWholeEllipse(shape) ? new XElement(Svg + "ellipse") : new XElement(Svg + "path");
    }

    // A rectangle and a whole ellipse keep SVG's own elements, which are shorter to read and easier
    // to edit by hand than the path each would otherwise be written as.
    private void Geometry(XElement element, PaintCodeShape shape)
    {
        var box = PaintCodePathData.Box(shape);

        if (element.Name == Svg + "rect")
        {
            element.SetAttributeValue("x", Number(box.X));
            element.SetAttributeValue("y", Number(box.Y));
            element.SetAttributeValue("width", Number(box.Width));
            element.SetAttributeValue("height", Number(box.Height));

            if (shape.Metrics.CornerRadius > 0)
            {
                element.SetAttributeValue("rx", Number(Math.Min(shape.Metrics.CornerRadius, Math.Min(box.Width, box.Height) / 2)));
            }

            return;
        }

        if (element.Name == Svg + "ellipse")
        {
            element.SetAttributeValue("cx", Number(box.X + box.Width / 2));
            element.SetAttributeValue("cy", Number(box.Y + box.Height / 2));
            element.SetAttributeValue("rx", Number(box.Width / 2));
            element.SetAttributeValue("ry", Number(box.Height / 2));

            return;
        }

        element.SetAttributeValue("d", PaintCodePathData.For(shape) ?? string.Empty);

        if (shape.IsEvenOdd)
        {
            element.SetAttributeValue("fill-rule", "evenodd");
        }
    }

    private void Fill(XElement element, PaintCodeShape shape)
    {
        switch (shape.Fill.Kind)
        {
            case PaintCodePaintKind.Color when shape.Fill.Color is { } color:
                element.SetAttributeValue("fill", Hex(color));
                Opacity(element, "fill-opacity", color.Alpha);

                break;

            case PaintCodePaintKind.Gradient when shape.Fill.Gradient is { } gradient:
                var first = gradient.Stops.Count > 0 ? gradient.Stops[0].Color : null;
                element.SetAttributeValue("fill", first is { } ? Hex(first) : "none");
                Note(PaintCodeImportSeverity.Approximated, shape.Name, "fill", $"the gradient '{gradient.Name}' is drawn as its first stop.");

                break;

            default:
                element.SetAttributeValue("fill", "none");

                break;
        }
    }

    private void Stroke(XElement element, PaintCodeShape shape)
    {
        if (shape.Stroke.Kind is not PaintCodePaintKind.Color || shape.Stroke.Color is not { } color)
        {
            return;
        }

        var style = shape.StrokeStyle;
        element.SetAttributeValue("stroke", Hex(color));
        Opacity(element, "stroke-opacity", color.Alpha);
        element.SetAttributeValue("stroke-width", Number(style.Width));

        if (Cap(style.Cap) is { } cap)
        {
            element.SetAttributeValue("stroke-linecap", cap);
        }

        if (Join(style.Join) is { } join)
        {
            element.SetAttributeValue("stroke-linejoin", join);
        }

        // SVG's default miter limit is 4 and PaintCode's is 10, so the value is written whenever a
        // mitred join could be affected by it rather than only when the author changed it.
        if (style.Join == 0 && Math.Abs(style.MiterLimit - 4) > 0.0001)
        {
            element.SetAttributeValue("stroke-miterlimit", Number(style.MiterLimit));
        }

        if (style.HasPattern)
        {
            element.SetAttributeValue("stroke-dasharray", $"{Number(style.Dash)} {Number(style.Gap)}");

            if (style.Phase != 0)
            {
                element.SetAttributeValue("stroke-dashoffset", Number(style.Phase));
            }
        }
    }

    private static string? Cap(int cap)
        => cap switch
        {
            1 => "round",
            2 => "square",
            _ => null
        };

    private static string? Join(int join)
        => join switch
        {
            1 => "round",
            2 => "bevel",
            _ => null
        };

    private static void Opacity(XElement element, string name, double alpha)
    {
        if (alpha < 1)
        {
            element.SetAttributeValue(name, Number(alpha));
        }
    }

    private static void Frame(XElement element, PaintCodeFrame frame, bool forGroup)
    {
        if (frame.Alpha < 1)
        {
            element.SetAttributeValue("opacity", Number(frame.Alpha));
        }

        if (frame.IsHidden || !frame.IsVisible)
        {
            element.SetAttributeValue("display", "none");
        }

        var turned = frame.Rotation != 0;
        var scaled = frame.ScaleX != 1 || frame.ScaleY != 1;

        if (forGroup && !turned && !scaled)
        {
            return;
        }

        var transform = new StringBuilder();
        var x = frame.Anchor.X;
        var y = -frame.Anchor.Y;

        if (x != 0 || y != 0 || turned || scaled)
        {
            transform.Append("translate(").Append(Number(x)).Append(',').Append(Number(y)).Append(')');
        }

        // The flip turns the drawing over, and an angle measured in it with it.
        if (turned)
        {
            transform.Append(" rotate(").Append(Number(-frame.Rotation)).Append(')');
        }

        if (scaled)
        {
            transform.Append(" scale(").Append(Number(frame.ScaleX)).Append(',').Append(Number(frame.ScaleY)).Append(')');
        }

        if (transform.Length > 0)
        {
            element.SetAttributeValue("transform", transform.ToString());
        }
    }

    private static string Hex(PaintCodeColor color)
        => string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", color.Red, color.Green, color.Blue);

    private static string Number(double value) => PaintCodePathData.Number(value);

    private void Note(PaintCodeImportSeverity severity, string element, string property, string message)
        => _notes.Add(new PaintCodeImportNote(severity, _canvas.Name, element, property, message));

    /// <summary>A document-unique id from a PaintCode name, which is unique only among its siblings.</summary>
    private string Identifier(string name)
    {
        var slug = PaintCodeSlug.Of(name);

        if (!_identifiers.TryGetValue(slug, out var seen))
        {
            _identifiers[slug] = 1;

            return slug;
        }

        _identifiers[slug] = seen + 1;

        return slug + "-" + (seen + 1).ToString(CultureInfo.InvariantCulture);
    }
}
