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

            // The clip shape is placed like any other shape. Transform rather than Frame: a clip has
            // no opacity and cannot be hidden, and copying those over would suppress the clip
            // wherever the shape it was drawn from is marked invisible.
            Transform(path, clip, null);
            _definitions.Add(new XElement(Svg + "clipPath", new XAttribute("id", identifier), path));
            element.SetAttributeValue("clip-path", $"url(#{identifier})");
        }

        // Only the canvas's own root group is exempt from carrying its anchor: that anchor is
        // (0, canvas height) rather than a position, and Document() and Expand() walk Root.Children
        // without ever reaching it. Every group below it stores its children relative to its own.
        Frame(element, group);

        return element;
    }

    private XElement Shape(PaintCodeShape shape)
    {
        var element = Element(shape);
        element.SetAttributeValue("id", Identifier(shape.Name));
        Geometry(element, shape);
        Fill(element, shape);
        Stroke(element, shape);

        var written = shape.Text is { } text ? WithText(element, shape, text) : element;
        Frame(written, shape);

        // One shape in the sample. Named rather than guessed at: PaintCode's own numbering is not
        // SVG's, and a blend mode that is nearly right is worse than one that is reported.
        if (shape.BlendMode != 0)
        {
            Note(PaintCodeImportSeverity.Dropped, shape.Name, "blendMode", $"PaintCode's blend mode {shape.BlendMode} has no name here, so the shape is drawn over what is under it.");
        }

        foreach (var property in new[] { "startAngle", "endAngle" })
        {
            if (shape.Bindings.ContainsKey(property))
            {
                Note(PaintCodeImportSeverity.Dropped, shape.Name, property, "the expression format keeps this value literal, so the drawing's own is written.");
            }
        }

        return written;
    }

    /// <summary>
    /// The shape and the text it carries, or the text alone where the shape paints nothing.
    /// </summary>
    /// <remarks>
    /// PaintCode lays text out itself, measuring the run and centring it in the shape's box; SVG
    /// places it from one point and an anchor. The two agree on where the box is and not on where
    /// the glyphs sit inside it, so every one of these is reported.
    /// </remarks>
    private XElement WithText(XElement element, PaintCodeShape shape, PaintCodeText text)
    {
        var box = PaintCodePathData.Box(shape);
        var run = new XElement(Svg + "text");
        var anchor = text.HorizontalAlignment switch
        {
            1 => "middle",
            2 => "end",
            _ => "start"
        };

        run.SetAttributeValue("x", Number(anchor switch
        {
            "middle" => box.X + box.Width / 2,
            "end" => box.X + box.Width - text.InsetHorizontal,
            _ => box.X + text.InsetHorizontal
        }));

        run.SetAttributeValue("y", Number(box.Y + box.Height / 2));
        run.SetAttributeValue("font-family", text.FontFamily);
        run.SetAttributeValue("font-size", Number(text.FontSize));

        if (text.FontFace.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            run.SetAttributeValue("font-weight", "bold");
        }

        if (text.FontFace.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            run.SetAttributeValue("font-style", "italic");
        }

        if (anchor != "start")
        {
            run.SetAttributeValue("text-anchor", anchor);
        }

        run.SetAttributeValue("dominant-baseline", "central");

        if (text.Color is { } color)
        {
            if (!Bind(run, "fill", shape, "fontColor") && Named(color) is { } name)
            {
                run.SetAttributeValue("fill", Braces(name));
                _code.Use(name);
            }
            else if (run.Attribute("fill") is null)
            {
                run.SetAttributeValue("fill", Hex(color));
                Opacity(run, "fill-opacity", color.Alpha);
            }
        }

        if (shape.Bindings.TryGetValue("text", out var binding) && binding.Expression is { } source)
        {
            if (PaintCodeExpressionTranslator.TryTranslate(source, _declarations, out var expression, out var refusal, _overrides))
            {
                run.Add(Braces(expression));
                _code.Use(expression);
            }
            else
            {
                Note(PaintCodeImportSeverity.Dropped, shape.Name, "text", refusal + ", so the words the drawing had are written.");
                run.Add(text.Value);
            }
        }
        else
        {
            run.Add(text.Value);
        }

        Note(PaintCodeImportSeverity.Approximated, shape.Name, "text", "the run is placed from the shape's box rather than measured the way PaintCode measures it.");

        if (element.Attribute("fill")?.Value == "none" && element.Attribute("stroke") is null)
        {
            return run;
        }

        return new XElement(Svg + "g", element, run);
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
        var scope = Closure(given);

        if (!_symbolIdentifiers.TryGetValue(key, out var identifier))
        {
            identifier = Expand(target, scope, key);
        }

        var element = new XElement(Svg + "use", new XAttribute("href", "#" + identifier));
        Frame(element, symbol, Fit(symbol, target));

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

    /// <summary>
    /// Everything the target reads differently because of what it was given.
    /// </summary>
    /// <remarks>
    /// Rebinding a variable rebinds every local built from it, and every local built from those. A
    /// copy that named the document's own local would read the caller's value of the very variable
    /// the instance replaced -- which is how a circle icon drew its glyph in the colour of the circle
    /// behind it.
    /// </remarks>
    private IReadOnlyDictionary<string, string> Closure(IReadOnlyDictionary<string, string> given)
    {
        if (given.Count == 0)
        {
            return given;
        }

        var scope = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in given)
        {
            scope[entry.Key] = entry.Value;
        }

        foreach (var name in _declarations.Locals())
        {
            if (scope.ContainsKey(name) ||
                _declarations.ByName[name] is not { Source: { } source } local ||
                !Reads(local.Body, scope))
            {
                continue;
            }

            if (PaintCodeExpressionTranslator.TryTranslate(source, _declarations, out var expression, out _, scope))
            {
                scope[name] = expression;
            }
        }

        return scope;
    }

    private static bool Reads(string? body, IReadOnlyDictionary<string, string> scope)
    {
        foreach (var name in PaintCodeDeclarations.Names(body ?? string.Empty))
        {
            if (scope.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What this instance gives its target, leaving out what it simply passes along.</summary>
    private IReadOnlyDictionary<string, string> Given(PaintCodeSymbolItem symbol)
    {
        var given = new Dictionary<string, string>(StringComparer.Ordinal);

        // A plain value first, so an expression written on the same name wins over it.
        foreach (var value in symbol.Values)
        {
            if (!value.Key.StartsWith(Virtual, StringComparison.Ordinal) || symbol.Bindings.ContainsKey(value.Key))
            {
                continue;
            }

            var name = PaintCodeSlug.Identifier(value.Key.Substring(Virtual.Length));

            if (!_declarations.ByName.TryGetValue(name, out var declaration) || declaration.Type is not { } type)
            {
                continue;
            }

            var literal = PaintCodeDeclarations.Literal(value.Value, type);

            // Only where it differs from what the name already means: a value equal to the default
            // changes nothing, and treating it as a rebinding would give every instance a copy of
            // its own rather than sharing one.
            if (literal is { } && literal != declaration.Body)
            {
                given[name] = literal;
            }
        }

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
                element.SetAttributeValue("fill", $"url(#{Gradient(shape, gradient)})");

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

        if (!Bind(element, "stroke-width", shape, "strokeWidth"))
        {
            element.SetAttributeValue("stroke-width", Number(style.Width));
        }

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

    /// <summary>
    /// A gradient in this drawing's defs, laid across the shape it fills.
    /// </summary>
    /// <remarks>
    /// PaintCode puts a gradient on a shape by an angle rather than by two points, and works the
    /// points out from the shape itself; measured against its own generated code, an angle on one of
    /// the axes lays the gradient across the shape's box exactly, and one off them does not — the
    /// shape's own middle is not its box's. Those are reported.
    ///
    /// The stops, not the gradient, are what an expression drives: the format has no gradient type
    /// and parameterises one through its stop colours instead.
    /// </remarks>
    private string Gradient(PaintCodeShape shape, PaintCodeGradient gradient)
    {
        var identifier = Identifier(PaintCodeSlug.Of(gradient.Name.Length > 0 ? gradient.Name : shape.Name + "-fill"));
        var radial = shape.Fill.Kind is PaintCodePaintKind.Gradient && shape.IsRadialFill;
        var angle = shape.FillGradientAngle;
        var stops = Stops(shape, gradient);
        var element = new XElement(
            Svg + (radial ? "radialGradient" : "linearGradient"),
            new XAttribute("id", identifier),
            new XAttribute("gradientUnits", "objectBoundingBox"));

        if (radial)
        {
            element.SetAttributeValue("cx", "0.5");
            element.SetAttributeValue("cy", "0.5");
            element.SetAttributeValue("r", "0.5");
            Note(PaintCodeImportSeverity.Approximated, shape.Name, "fill", $"the radial gradient '{gradient.Name}' is laid over the shape's box rather than where PaintCode centres it.");
        }
        else
        {
            // The angle points the way PaintCode measures it, which the flip turns over.
            var radians = -angle * Math.PI / 180;
            var dx = Math.Cos(radians) / 2;
            var dy = Math.Sin(radians) / 2;

            element.SetAttributeValue("x1", Number(0.5 - dx));
            element.SetAttributeValue("y1", Number(0.5 - dy));
            element.SetAttributeValue("x2", Number(0.5 + dx));
            element.SetAttributeValue("y2", Number(0.5 + dy));

            if (Math.Abs(angle % 90) > 0.001)
            {
                Note(PaintCodeImportSeverity.Approximated, shape.Name, "fill", $"the gradient '{gradient.Name}' runs at {Number(angle)} degrees, which is laid across the shape's box rather than where PaintCode puts it.");
            }
        }

        foreach (var stop in stops)
        {
            element.Add(stop);
        }

        _definitions.Add(element);

        return identifier;
    }

    private IEnumerable<XElement> Stops(PaintCodeShape shape, PaintCodeGradient gradient)
    {
        var driven = shape.Bindings.TryGetValue("fill", out var binding) && binding.Expression is { } source &&
                     _declarations.TryStops(source, _overrides, out var expressions, out var refusal)
            ? expressions
            : null;

        if (driven is null && shape.Bindings.TryGetValue("fill", out var unbound) && unbound.Expression is { } text)
        {
            _declarations.TryStops(text, _overrides, out _, out var why);
            Note(PaintCodeImportSeverity.Dropped, shape.Name, "fill", $"the gradient is not driven: {why}.");
        }

        for (var index = 0; index < gradient.Stops.Count; index++)
        {
            var stop = gradient.Stops[index];
            var element = new XElement(
                Svg + "stop",
                new XAttribute("offset", Number(stop.Location)));

            if (driven is { } bound && index < bound.Count)
            {
                element.SetAttributeValue("stop-color", Braces(bound[index]));
                _code.Use(bound[index]);
            }
            else
            {
                var colour = _declarations.Stop(stop.Color);

                if (colour.StartsWith("#", StringComparison.Ordinal))
                {
                    element.SetAttributeValue("stop-color", Hex(stop.Color));
                    Opacity(element, "stop-opacity", stop.Color.Alpha);
                }
                else
                {
                    element.SetAttributeValue("stop-color", Braces(colour));
                    _code.Use(colour);
                }
            }

            yield return element;
        }
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

    /// <summary>
    /// Whether this group is drawn into a layer of its own, which a driven transform cannot cross.
    /// </summary>
    private static bool Opens(PaintCodeGroup group)
        => group.Frame.Alpha < 1 || group.Bindings.ContainsKey("alpha");

    private void Frame(XElement element, PaintCodeItem item, PaintCodePoint? fit = null)
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

        Transform(element, item, fit);
    }

    private void Transform(XElement element, PaintCodeItem item, PaintCodePoint? fit)
    {
        var frame = item.Frame;
        var turned = frame.Rotation != 0 || item.Bindings.ContainsKey("displayRotation");
        var scaled = frame.ScaleX != 1 || frame.ScaleY != 1 ||
                     item.Bindings.ContainsKey("displayScaleX") || item.Bindings.ContainsKey("displayScaleY") ||
                     (fit is { } fitScale && (fitScale.X != 1 || fitScale.Y != 1));

        var transform = new StringBuilder();
        var x = frame.Anchor.X;
        var y = -frame.Anchor.Y;

        if (x != 0 || y != 0 || turned || scaled ||
            item.Bindings.ContainsKey("displayAnchorX") || item.Bindings.ContainsKey("displayAnchorY"))
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

        if (frame.ScaleX != 1 || frame.ScaleY != 1 ||
            item.Bindings.ContainsKey("displayScaleX") || item.Bindings.ContainsKey("displayScaleY"))
        {
            transform.Append(" scale(")
                .Append(Argument(item, "displayScaleX", frame.ScaleX, item.Name))
                .Append(',')
                .Append(Argument(item, "displayScaleY", frame.ScaleY, item.Name))
                .Append(')');
        }

        // A shape carries its box offset in its own path data, so only a use needs it written out —
        // and it goes after the turn, because the turn is about the anchor and not about the box.
        if (fit is { } fitted)
        {
            var box = new PaintCodePoint(frame.X, -(frame.Y + frame.Height));

            // Against what would be written rather than against zero: sixteen instances carry a
            // corner a hundredth of a millionth of a unit off it, and a translate of nought is noise.
            if (Number(box.X) != "0" || Number(box.Y) != "0")
            {
                transform.Append(" translate(").Append(Number(box.X)).Append(',').Append(Number(box.Y)).Append(')');
            }

            if (fitted.X != 1 || fitted.Y != 1)
            {
                transform.Append(" scale(").Append(Number(fitted.X)).Append(',').Append(Number(fitted.Y)).Append(')');
            }
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

        if (Produced(expression, item, property, name) is not { } produced)
        {
            return Number(value);
        }

        // Rounded before it is compared: the subtrahend comes from a float-backed evaluator, so a
        // constant that writes as nought is nought, and adding it would say something it does not mean.
        var offset = Number(value - produced);

        return offset == "0" ? Braces(expression) : Braces($"{expression} + {offset}");
    }

    private string Angle(PaintCodeItem item, double rotation)
    {
        if (Expression(item, "displayRotation", item.Name) is not { } expression)
        {
            return Number(-rotation);
        }

        if (Produced(expression, item, "displayRotation", item.Name) is not { } produced)
        {
            return Number(-rotation);
        }

        // Worked out in PaintCode's own space and turned over afterwards, since that is the space
        // both the angle and the number stored beside it are measured in.
        var offset = Number(rotation - produced);

        return Braces(offset == "0" ? $"-({expression})" : $"-(({expression}) + {offset})");
    }

    /// <summary>
    /// What the expression itself comes to, which is what the offset is measured from.
    /// </summary>
    /// <remarks>
    /// Null where it cannot be worked out, and the caller then writes the number the drawing had:
    /// driving it by a constant nobody could compute would move it somewhere nothing chose.
    /// </remarks>
    private double? Produced(string expression, PaintCodeItem item, string property, string name)
    {
        if (_declarations.TryValue(expression, out var produced))
        {
            return produced;
        }

        Note(PaintCodeImportSeverity.Dropped, name, property, "the expression's own value could not be worked out, so the drawing's own value is written.");

        return null;
    }

    /// <summary>The translated expression driving <paramref name="property"/>, or null where none can.</summary>
    private string? Expression(PaintCodeItem item, string property, string name)
    {
        if (!item.Bindings.TryGetValue(property, out var binding) || binding.Expression is not { } source)
        {
            return null;
        }

        // A layer's bounds were unioned from where its children painted when the drawing was
        // recorded, and it clips to them -- so neither a move nor a wider stroke can cross one, and
        // under one the number is written instead.
        if (_layers > 0 && (property.StartsWith("display", StringComparison.Ordinal) || property == "strokeWidth"))
        {
            Note(PaintCodeImportSeverity.Dropped, name, property, "this cannot be driven inside a group that draws into a layer, so the drawing's own value is written.");

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
