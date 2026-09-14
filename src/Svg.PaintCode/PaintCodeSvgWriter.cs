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
    private readonly PaintCodeDeclarations _declarations;
    private readonly PaintCodeSymbols _symbols;
    private readonly PaintCodeCode _code;
    private readonly Dictionary<string, int> _identifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _symbolIdentifiers = new(StringComparer.Ordinal);
    private readonly List<string> _expanding = new();
    private readonly List<XElement> _definitions = new();
    private IReadOnlyDictionary<string, string>? _overrides;
    private int _layers;

    private PaintCodeSvgWriter(PaintCodeCanvas canvas, PaintCodeDeclarations declarations, PaintCodeSymbols symbols, ICollection<PaintCodeImportNote> notes)
    {
        _canvas = canvas;
        _declarations = declarations;
        _symbols = symbols;
        _notes = notes;
        _code = new PaintCodeCode(declarations);
    }

    internal static XDocument Write(
        PaintCodeCanvas canvas,
        PaintCodeDeclarations declarations,
        PaintCodeSymbols symbols,
        ICollection<PaintCodeImportNote> notes)
        => new PaintCodeSvgWriter(canvas, declarations, symbols, notes).Document();

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

        // The block goes in <defs> with everything else a drawing declares, and is written after the
        // tree because only walking it says which names the drawing actually reaches.
        var code = _code.Element();

        if (_definitions.Count > 0 || code is { })
        {
            var definitions = new XElement(Svg + "defs", _definitions);

            if (code is { })
            {
                root.SetAttributeValue(XNamespace.Xmlns + "e", PaintCodeCode.Namespace.NamespaceName);
                definitions.Add(code);
            }

            root.Add(definitions);
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
        var opens = Opens(group);

        _layers += opens ? 1 : 0;

        foreach (var child in group.Children)
        {
            if (Item(child) is { } converted)
            {
                children.Add(converted);
            }
        }

        _layers -= opens ? 1 : 0;

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
        Frame(element, group, forGroup: true);

        return element;
    }

    private XElement Shape(PaintCodeShape shape)
    {
        var element = Element(shape);
        element.SetAttributeValue("id", Identifier(shape.Name));
        Geometry(element, shape);
        Fill(element, shape);
        Stroke(element, shape);
        Frame(element, shape, forGroup: false);

        if (shape.Text is { } text)
        {
            Note(PaintCodeImportSeverity.Dropped, shape.Name, "text", $"'{text.Value}' is not written yet.");
        }

        foreach (var property in new[] { "strokeWidth", "startAngle", "endAngle" })
        {
            if (shape.Bindings.ContainsKey(property))
            {
                Note(PaintCodeImportSeverity.Dropped, shape.Name, property, "the expression format keeps this value literal, so the drawing's own is written.");
            }
        }

        return element;
    }

    /// <summary>
    /// A symbol instance: a <c>&lt;use&gt;</c> of the target's tree, written into this drawing's own
    /// defs once per distinct set of the values it is given.
    /// </summary>
    /// <remarks>
    /// A use cannot pass anything, so an instance that rebinds one of the target's variables cannot
    /// share a copy with one that does not: what is shared is keyed by the target and by what it is
    /// given. Most instances give nothing -- they name the same variables the target already reads --
    /// and those all share one.
    ///
    /// The copy is written into this file rather than referenced across files. A use that crossed one
    /// would be an external reference, which svgc cannot follow: it compiles one drawing at a time.
    /// </remarks>
    private XElement? Symbol(PaintCodeSymbolItem symbol)
    {
        if (_symbols.Find(symbol) is not { } target)
        {
            Note(PaintCodeImportSeverity.Dropped, symbol.Name, "symbol", $"'{symbol.TargetName}' names a canvas this document does not hold.");

            return null;
        }

        var given = Given(symbol);
        var key = target.Identifier + "(" + string.Join(",", Ordered(given)) + ")";

        if (!_symbolIdentifiers.TryGetValue(key, out var identifier))
        {
            identifier = Expand(target, given, key);
        }

        var element = new XElement(Svg + "use", new XAttribute("href", "#" + identifier));
        Frame(element, symbol, forGroup: false, Fit(symbol, target));

        // PaintCode clips a symbol to the box it was placed in, so anything the target draws outside
        // its own canvas is cut off rather than spilling into this drawing.
        element.SetAttributeValue("clip-path", $"url(#{identifier}-clip)");

        return element;
    }

    private string Expand(PaintCodeCanvas target, IReadOnlyDictionary<string, string> given, string key)
    {
        if (_expanding.Contains(target.Identifier))
        {
            throw new PaintCodeException(
                $"The symbol '{target.Name}' contains itself, through {string.Join(" -> ", _expanding)}.");
        }

        var identifier = Identifier("sym-" + PaintCodeSlug.Of(target.Name));
        var outer = _overrides;
        var layers = _layers;

        _expanding.Add(target.Identifier);
        _overrides = given.Count == 0 ? null : given;

        // The target's own tree stands on its own: a layer the caller opened is above the use, not
        // above the copy, and the copy is written once for every caller.
        _layers = 0;

        var children = new List<XElement>();

        foreach (var child in target.Root.Children)
        {
            if (Item(child) is { } converted)
            {
                children.Add(converted);
            }
        }

        _layers = layers;
        _overrides = outer;
        _expanding.RemoveAt(_expanding.Count - 1);

        _symbolIdentifiers[key] = identifier;
        _definitions.Add(new XElement(Svg + "g", new XAttribute("id", identifier), children));
        _definitions.Add(new XElement(
            Svg + "clipPath",
            new XAttribute("id", identifier + "-clip"),
            new XElement(
                Svg + "rect",
                new XAttribute("width", Number(target.Bounds.Width)),
                new XAttribute("height", Number(target.Bounds.Height)))));

        return identifier;
    }

    /// <summary>What this instance rebinds, leaving out the names it simply passes along.</summary>
    private IReadOnlyDictionary<string, string> Given(PaintCodeSymbolItem symbol)
    {
        var given = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var binding in symbol.Bindings)
        {
            if (!binding.Key.StartsWith(Virtual, StringComparison.Ordinal) || binding.Value.Expression is not { } source)
            {
                continue;
            }

            var name = PaintCodeSlug.Identifier(binding.Key.Substring(Virtual.Length));

            if (!PaintCodeExpressionTranslator.TryTranslate(source, _declarations, out var expression, out var refusal, _overrides))
            {
                Note(PaintCodeImportSeverity.Dropped, symbol.Name, binding.Key, refusal + ", so the symbol reads the value the drawing already has.");

                continue;
            }

            // Passing a name along to itself is the same as passing nothing, and saying so is what
            // lets 1848 of the sample's 2098 bindings share one copy of what they point at.
            if (expression != name)
            {
                given[name] = expression;
                _code.Use(expression);
            }
        }

        return given;
    }

    private const string Virtual = "VIRTUAL__";

    private static IEnumerable<string> Ordered(IReadOnlyDictionary<string, string> given)
    {
        var names = new List<string>(given.Keys);
        names.Sort(StringComparer.Ordinal);

        foreach (var name in names)
        {
            yield return name + "=" + given[name];
        }
    }

    /// <summary>How much the target has to be scaled to fill the box the instance was placed in.</summary>
    private static PaintCodePoint Fit(PaintCodeSymbolItem symbol, PaintCodeCanvas target)
        => new(
            target.Bounds.Width == 0 ? 1 : symbol.Frame.Width / target.Bounds.Width,
            target.Bounds.Height == 0 ? 1 : symbol.Frame.Height / target.Bounds.Height);

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
                Paint(element, "fill", shape, "fill", color);

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
        Paint(element, "stroke", shape, "strokeColor", color);
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

    /// <summary>
    /// A colour attribute: what drives it, the library colour it is, or the bytes themselves.
    /// </summary>
    /// <remarks>
    /// Only the last of the three carries a separate opacity. The other two are expressions of type
    /// colour, whose alpha is already in the value they produce — writing one beside them would
    /// apply it twice.
    /// </remarks>
    private void Paint(XElement element, string attribute, PaintCodeShape shape, string property, PaintCodeColor color)
    {
        if (Bind(element, attribute, shape, property))
        {
            return;
        }

        if (Named(color) is { } name)
        {
            element.SetAttributeValue(attribute, Braces(name));
            _code.Use(name);

            return;
        }

        element.SetAttributeValue(attribute, Hex(color));
        Opacity(element, attribute + "-opacity", color.Alpha);
    }

    /// <summary>The declaration this colour is, where the library names it and a drawing can reach it.</summary>
    private string? Named(PaintCodeColor color)
    {
        if (color.Name.Length == 0)
        {
            return null;
        }

        var name = PaintCodeSlug.Identifier(color.Name);

        if (_overrides is { } overrides && overrides.TryGetValue(name, out var given))
        {
            return given;
        }

        return _declarations.ByName.TryGetValue(name, out var declaration) &&
               declaration.Kind is PaintCodeDeclarationKind.Parameter or PaintCodeDeclarationKind.Local
            ? name
            : null;
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

    /// <summary>Whether this group is drawn into a layer of its own, which a driven transform cannot cross.</summary>
    private static bool Opens(PaintCodeGroup group)
        => group.Frame.Alpha < 1 || group.Bindings.ContainsKey("alpha");

    private void Frame(XElement element, PaintCodeItem item, bool forGroup, PaintCodePoint? fit = null)
    {
        var frame = item.Frame;

        if (!Bind(element, "opacity", item, "alpha") && frame.Alpha < 1)
        {
            element.SetAttributeValue("opacity", Number(frame.Alpha));
        }

        // display rather than visibility: a hidden PaintCode item contributes no drawing at all,
        // which is what display means and what visibility does not.
        if (frame.IsHidden)
        {
            element.SetAttributeValue("display", "none");
        }
        else if (!Bind(element, "display", item, "visibilityMode") && !frame.IsVisible)
        {
            element.SetAttributeValue("display", "none");
        }

        Transform(element, item, forGroup, fit);
    }

    private void Transform(XElement element, PaintCodeItem item, bool forGroup, PaintCodePoint? fit)
    {
        var frame = item.Frame;
        var turned = frame.Rotation != 0 || item.Bindings.ContainsKey("displayRotation");
        var scaled = frame.ScaleX != 1 || frame.ScaleY != 1 ||
                     item.Bindings.ContainsKey("displayScaleX") || item.Bindings.ContainsKey("displayScaleY") ||
                     (fit is { } size && (size.X != 1 || size.Y != 1));
        var moved = item.Bindings.ContainsKey("displayAnchorX") || item.Bindings.ContainsKey("displayAnchorY");

        if (forGroup && !turned && !scaled && !moved)
        {
            return;
        }

        var transform = new StringBuilder();
        var x = frame.Anchor.X;
        var y = -frame.Anchor.Y;

        if (x != 0 || y != 0 || turned || scaled || moved)
        {
            transform.Append("translate(")
                .Append(Argument(item, "displayAnchorX", x, item.Name))
                .Append(',')
                .Append(Argument(item, "displayAnchorY", y, item.Name))
                .Append(')');
        }

        // The flip turns the drawing over, and an angle measured in it with it.
        if (turned)
        {
            transform.Append(" rotate(").Append(Angle(item, frame.Rotation)).Append(')');
        }

        if (scaled)
        {
            transform.Append(" scale(")
                .Append(Argument(item, "displayScaleX", frame.ScaleX * (fit?.X ?? 1), item.Name))
                .Append(',')
                .Append(Argument(item, "displayScaleY", frame.ScaleY * (fit?.Y ?? 1), item.Name))
                .Append(')');
        }

        if (transform.Length > 0)
        {
            element.SetAttributeValue("transform", transform.ToString());
        }
    }

    /// <summary>
    /// One argument of a transform: the drawing's own number, or the expression driving it.
    /// </summary>
    /// <remarks>
    /// PaintCode's display properties are measured from whatever the item sits in, and the value the
    /// file was saved with says how far that is: the difference between the number this drawing needs
    /// and the number the expression produced is a constant, and adding it back is what PaintCode's
    /// own generated code does.
    /// </remarks>
    private string Argument(PaintCodeItem item, string property, double value, string name)
    {
        if (Expression(item, property, name) is not { } expression)
        {
            return Number(value);
        }

        var offset = value - (item.Bindings[property].Number ?? value);

        return offset == 0 ? Braces(expression) : Braces($"{expression} + {Number(offset)}");
    }

    private string Angle(PaintCodeItem item, double rotation)
    {
        if (Expression(item, "displayRotation", item.Name) is not { } expression)
        {
            return Number(-rotation);
        }

        var offset = rotation - (item.Bindings["displayRotation"].Number ?? rotation);

        return Braces(offset == 0 ? $"-({expression})" : $"-(({expression}) + {Number(offset)})");
    }

    /// <summary>The translated expression driving <paramref name="property"/>, or null where none can.</summary>
    private string? Expression(PaintCodeItem item, string property, string name)
    {
        if (!item.Bindings.TryGetValue(property, out var binding) || binding.Expression is not { } source)
        {
            return null;
        }

        // A transform is rewritten in the recorded drawing, and a layer's bounds were measured from
        // where its children were when it was recorded -- so under one, the number is written instead.
        if (_layers > 0 && property.StartsWith("display", StringComparison.Ordinal))
        {
            Note(PaintCodeImportSeverity.Dropped, name, property, "a transform cannot be driven inside a group that draws into a layer, so the drawing's own value is written.");

            return null;
        }

        if (!PaintCodeExpressionTranslator.TryTranslate(source, _declarations, out var expression, out var refusal, _overrides))
        {
            Note(PaintCodeImportSeverity.Dropped, name, property, refusal + ", so the drawing's own value is written.");

            return null;
        }

        _code.Use(expression);

        return expression;
    }

    /// <summary>Binds <paramref name="attribute"/> to what drives <paramref name="property"/>.</summary>
    private bool Bind(XElement element, string attribute, PaintCodeItem item, string property)
    {
        if (Expression(item, property, item.Name) is not { } expression)
        {
            return false;
        }

        element.SetAttributeValue(attribute, Braces(expression));

        return true;
    }

    private static string Braces(string expression) => "{{ " + expression + " }}";

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
