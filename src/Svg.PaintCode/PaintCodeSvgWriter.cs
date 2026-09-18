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
            var path = new XElement(Svg + "path");

            // A clip carries a driven sweep as readily as a drawn shape does: a level indicator is
            // very often an arc masking what is under it.
            var sweeping = Sweep(clip, clipped: true);

            if (sweeping is { } dashed)
            {
                // A clip path takes fill geometry and a stroke is not part of it, so the wedge a
                // dash draws has to mask rather than clip. White against nothing, which is opaque
                // under any luminance, and the region is the circle's own box so the mask cuts
                // exactly where the clip did rather than at the fifth the default would add.
                var box = PaintCodePathData.Box(clip);
                var mask = Identifier(clip.Name + "-mask");

                path.SetAttributeValue("d", dashed.Data);
                path.SetAttributeValue("fill", "none");
                path.SetAttributeValue("stroke", "#ffffff");
                path.SetAttributeValue("stroke-width", Number(dashed.Width ?? 0));
                path.SetAttributeValue("stroke-dasharray", $"{Number(dashed.Circumference)} {Number(dashed.Circumference)}");
                path.SetAttributeValue("stroke-dashoffset", dashed.Offset);

                Transform(path, clip, null);

                _definitions.Add(new XElement(
                    Svg + "mask",
                    new XAttribute("id", mask),
                    new XAttribute("maskUnits", "userSpaceOnUse"),
                    new XAttribute("x", Number(box.X - 1)),
                    new XAttribute("y", Number(box.Y - 1)),
                    new XAttribute("width", Number(box.Width + 2)),
                    new XAttribute("height", Number(box.Height + 2)),
                    path));

                element.SetAttributeValue("mask", $"url(#{mask})");
            }
            else
            {
                var identifier = Identifier(clip.Name + "-clip");

                Geometry(path, clip);

                // The clip shape is placed like any other shape. Transform rather than Frame: a clip
                // has no opacity and cannot be hidden, and copying those over would suppress the clip
                // wherever the shape it was drawn from is marked invisible.
                Transform(path, clip, null);

                _definitions.Add(new XElement(Svg + "clipPath", new XAttribute("id", identifier), path));
                element.SetAttributeValue("clip-path", $"url(#{identifier})");
            }
        }

        // Only the canvas's own root group is exempt from carrying its anchor: that anchor is
        // (0, canvas height) rather than a position, and Document() and Expand() walk Root.Children
        // without ever reaching it. Every group below it stores its children relative to its own.
        Frame(element, group);

        return element;
    }

    private XElement Shape(PaintCodeShape shape)
    {
        // Asked first, because a driven sweep decides what the element is: the whole circle it is
        // drawn as is a path, whatever the angles it was saved with would otherwise have made it.
        var sweeping = Sweep(shape);
        var element = sweeping is { } ? new XElement(Svg + "path") : Element(shape);

        element.SetAttributeValue("id", Identifier(shape.Name));

        if (sweeping is { } dashed)
        {
            Dashed(element, shape, dashed);
        }
        else
        {
            Geometry(element, shape);
            Fill(element, shape);
            Stroke(element, shape);
        }

        var written = shape.Text is { } text ? WithText(element, shape, text) : element;
        Frame(written, shape);

        // One shape in the sample. Named rather than guessed at: PaintCode's own numbering is not
        // SVG's, and a blend mode that is nearly right is worse than one that is reported.
        if (shape.BlendMode != 0)
        {
            Note(PaintCodeImportSeverity.Dropped, shape.Name, "blendMode", $"PaintCode's blend mode {shape.BlendMode} has no name here, so the shape is drawn over what is under it.");
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
            Note(PaintCodeImportSeverity.Missing, symbol.Name, "symbol", $"the document has no canvas called '{symbol.TargetName}', so nothing is drawn where this symbol was placed.");

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
            if (scope.ContainsKey(name) || _declarations.ByName[name] is not { } local || !Reads(local.Body, scope))
            {
                continue;
            }

            // A local built from a library colour rather than from a PaintCode expression -- an
            // alpha over a parameter, say -- is already written in this format's own syntax and has
            // no source to translate again. It still has to follow a rebinding: sr-limitTemperature
            // hands its symbol one colour and the symbol derives another from it, and reading the
            // canvas's own instead left the glyph in the wrong shade at thirteen of sixteen settings.
            if (local.Source is not { } source)
            {
                if (local.Body is { } body)
                {
                    scope[name] = PaintCodeDeclarations.Rebound(body, scope);
                }

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

            // A colour is stored as the library colour it was set from, name and all, so one named
            // after a variable is a reference to that variable rather than a value -- which is why
            // PaintCode's own code hands whiteColor_ along by name while pinning the booleans beside
            // it to constants. Pinning the literal instead would freeze the symbol at whatever the
            // caller's colours happened to default to.
            // ...and only where that variable is one the drawing can actually name. A library colour
            // nobody marked as used is a constant, which PaintCode bakes too -- drawSymboloverlayadd
            // takes no colorBlue, it draws the colour.
            if (value.Value.Color is { } colour && colour.Name.Length > 0
                && PaintCodeSlug.Identifier(colour.Name) is { } referenced
                && _declarations.ByName.TryGetValue(referenced, out var target)
                && target.Kind is PaintCodeDeclarationKind.Parameter or PaintCodeDeclarationKind.Local)
            {
                if (referenced != name)
                {
                    given[name] = referenced;
                    _code.Use(referenced);
                }

                continue;
            }

            var literal = PaintCodeDeclarations.Literal(value.Value, type);

            // Everything else is a value the instance pins. A parameter's body is its default rather
            // than its value, so pin it even where the two read alike: the caller can still be drawn
            // with anything, and the symbol is no longer following it. Reading those as the same
            // thing drew fence-state's lock in the caller's theme -- PaintCode pins isLight to false
            // there, and false is also isLight's default, so the pin was discarded. Only a local
            // already fixed to that same text really does mean the same thing.
            if (literal is { } && (declaration.Kind is PaintCodeDeclarationKind.Parameter || literal != declaration.Body))
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

    /// <summary>The circle a driven arc is drawn as, and the dash that cuts it to length.</summary>
    /// <remarks>
    /// A wedge takes the shape's fill on its stroke, since the stroke is what draws it -- which
    /// keeps a driven colour driven, because the same Paint writes it either way.
    /// </remarks>
    private void Dashed(XElement element, PaintCodeShape shape, Sweeping sweeping)
    {
        element.SetAttributeValue("d", sweeping.Data);

        if (sweeping.Width is { } width)
        {
            element.SetAttributeValue("fill", "none");

            if (shape.Fill.Color is { } color)
            {
                Paint(element, "stroke", shape, "fill", color);
            }

            element.SetAttributeValue("stroke-width", Number(width));
        }
        else
        {
            Fill(element, shape);
            Stroke(element, shape);
        }

        element.SetAttributeValue("stroke-dasharray", $"{Number(sweeping.Circumference)} {Number(sweeping.Circumference)}");
        element.SetAttributeValue("stroke-dashoffset", sweeping.Offset);
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

        if (radial && shape.FillGradientEnds is { } circles)
        {
            // Between two circles, which is what PaintCode lays a radial one between and what
            // cx/cy/r with fx/fy/fr says exactly: the outer circle is where the last stop lands and
            // the inner one is where the first does. Laying it over the box instead put every one of
            // these in the middle at half the width, whatever PaintCode had been told.
            var middle = Middle(shape);

            element.SetAttributeValue("gradientUnits", "userSpaceOnUse");
            element.SetAttributeValue("cx", Number(middle.X + circles.End.X));
            element.SetAttributeValue("cy", Number(middle.Y - circles.End.Y));
            element.SetAttributeValue("r", Number(circles.EndRadius));
            element.SetAttributeValue("fx", Number(middle.X + circles.Start.X));
            element.SetAttributeValue("fy", Number(middle.Y - circles.Start.Y));
            element.SetAttributeValue("fr", Number(circles.StartRadius));
        }
        else if (radial)
        {
            element.SetAttributeValue("cx", "0.5");
            element.SetAttributeValue("cy", "0.5");
            element.SetAttributeValue("r", "0.5");
            Note(PaintCodeImportSeverity.Approximated, shape.Name, "fill", $"the radial gradient '{gradient.Name}' is laid over the shape's box rather than where PaintCode centres it.");
        }
        else if (shape.FillGradientEnds is { } ends)
        {
            // Laid by its two ends rather than by an angle, so the ends are where it goes: they are
            // offsets from the shape's own middle, which is what PaintCode works its own two points
            // out from, and in its own space, so the flip turns them over. Said in the shape's own
            // coordinates rather than across its box -- the box is only the shape's extent, and
            // these run past it as often as not.
            var middle = Middle(shape);

            element.SetAttributeValue("gradientUnits", "userSpaceOnUse");
            element.SetAttributeValue("x1", Number(middle.X + ends.Start.X));
            element.SetAttributeValue("y1", Number(middle.Y - ends.Start.Y));
            element.SetAttributeValue("x2", Number(middle.X + ends.End.X));
            element.SetAttributeValue("y2", Number(middle.Y - ends.End.Y));
        }
        else if (Laid(shape, angle) is { } laid)
        {
            // Across the shape rather than across its box, which is what PaintCode draws.
            element.SetAttributeValue("gradientUnits", "userSpaceOnUse");
            element.SetAttributeValue("x1", Number(laid.Start.X));
            element.SetAttributeValue("y1", Number(laid.Start.Y));
            element.SetAttributeValue("x2", Number(laid.End.X));
            element.SetAttributeValue("y2", Number(laid.End.Y));
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

    /// <summary>
    /// The two ends of a gradient given an angle, or null for an outline this cannot measure.
    /// </summary>
    /// <remarks>
    /// PaintCode draws one from one side of the shape to the other along the angle -- not across the
    /// shape's box, which is the same thing only for a plain rectangle. Its own generated code says
    /// so: tv-state's rounded rectangle, 18.85 by 9.85 at -45 degrees, is drawn 8.91 out from the
    /// middle rather than the 10.15 its box's corner is at.
    ///
    /// Only the reach along the direction matters to a linear gradient, so the ends are written on a
    /// line through the shape's middle -- any line along it paints identically.
    /// </remarks>
    private static (PaintCodePoint Start, PaintCodePoint End)? Laid(PaintCodeShape shape, double angle)
    {
        // The angle points the way PaintCode measures it, which the flip turns over.
        var radians = -angle * Math.PI / 180;
        var direction = new PaintCodePoint(Math.Cos(radians), Math.Sin(radians));

        // A reach of nothing is a shape with no outline along the angle, which nothing can be laid
        // between: SVG paints a gradient of zero length in its last stop's colour alone.
        if (PaintCodePathData.Span(shape, direction) is not { } span || span.High - span.Low < 1e-6)
        {
            return null;
        }

        var middle = Middle(shape);
        var at = (middle.X * direction.X) + (middle.Y * direction.Y);

        return (Along(middle, direction, span.Low - at), Along(middle, direction, span.High - at));
    }

    private static PaintCodePoint Along(PaintCodePoint from, PaintCodePoint direction, double by)
        => new(from.X + (direction.X * by), from.Y + (direction.Y * by));

    /// <summary>The shape's own middle, which its gradient handles are measured from.</summary>
    private static PaintCodePoint Middle(PaintCodeShape shape)
    {
        var box = PaintCodePathData.Box(shape);

        return new PaintCodePoint(box.X + (box.Width / 2), box.Y + (box.Height / 2));
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

        if (Produced(item, property, name) is not { } produced)
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

        if (Produced(item, "displayRotation", item.Name) is not { } produced)
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
    /// Measured against what the canvas says on its own, never against what a caller gave it: the
    /// offset is the gap between the expression and the number saved beside it, and that gap belongs
    /// to the canvas, so every instance of a symbol has to see the same one. Measuring the
    /// substituted form instead left nothing free in it, so the offset came to exactly minus the
    /// expression and the two cancelled -- the instance drew at the pose it was saved in and its
    /// argument did nothing. A tank at a twentieth full drew full.
    ///
    /// Null where it cannot be worked out, and the caller then writes the number the drawing had:
    /// driving it by a constant nobody could compute would move it somewhere nothing chose.
    /// </remarks>
    private double? Produced(PaintCodeItem item, string property, string name)
    {
        if (item.Bindings.TryGetValue(property, out var binding) &&
            binding.Expression is { } source &&
            PaintCodeExpressionTranslator.TryTranslate(source, _declarations, out var alone, out _) &&
            _declarations.TryValue(alone, out var produced))
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

    /// <summary>An arc whose sweep an expression drives, as the dashed circle that draws it.</summary>
    /// <remarks>
    /// <see cref="Data"/> is the whole circle from the arc's own start; <see cref="Offset"/> is what
    /// the dash is shifted by, which is the part of the circle the arc does not cover;
    /// <see cref="Width"/> is set for a wedge, which is drawn as a circle of half the radius stroked
    /// its whole width, and null for an arc that was already a stroke.
    /// </remarks>
    private readonly struct Sweeping
    {
        internal Sweeping(string data, double circumference, string offset, double? width)
        {
            Data = data;
            Circumference = circumference;
            Offset = offset;
            Width = width;
        }

        internal string Data { get; }

        internal double Circumference { get; }

        internal string Offset { get; }

        internal double? Width { get; }
    }

    /// <summary>
    /// How to draw an oval whose sweep an expression drives, or null with a note saying why not.
    /// </summary>
    /// <remarks>
    /// PaintCode turns an arc from a fixed angle to a driven one; SVG bakes an arc into path data,
    /// where no expression can reach. A circle dashed at its own circumference draws the same arc,
    /// because what a dash leaves showing is a length along the path and a length along a circle is
    /// an angle -- so the sweep becomes the dash's offset, which an expression <em>can</em> drive.
    ///
    /// Only a circle. On an ellipse arc length is not proportional to angle, the outline offset by
    /// half a stroke is not an ellipse, and neither the rim nor a wedge's straight edges would land
    /// where PaintCode puts them.
    /// </remarks>
    private Sweeping? Sweep(PaintCodeShape shape, bool clipped = false)
    {
        var start = shape.Bindings.ContainsKey("startAngle");
        var end = shape.Bindings.ContainsKey("endAngle");

        if (!start && !end)
        {
            return null;
        }

        var property = start ? "startAngle" : "endAngle";
        var metrics = shape.Metrics;

        // A clip is geometry, so what it is filled or stroked with says nothing about it: it is the
        // wedge its own closed flag makes it.
        var stroked = !clipped && shape.Stroke.Kind is PaintCodePaintKind.Color;
        var filled = clipped || shape.Fill.Kind is not PaintCodePaintKind.None;

        string? refusal =
            !PaintCodePathData.IsRound(shape)
                ? "an ellipse's arc is not proportional to its angle, so a dash cannot place its end"
            : start && end
                ? "both ends of the arc are driven and a dash can only measure one of them"
            : metrics.IsClosed && stroked
                ? "a closed arc is outlined round its two straight edges as well, which one dashed circle cannot draw"
            : !metrics.IsClosed && !stroked
                ? "an open arc closes across its chord when it is filled, which a dash cannot draw"
            : stroked && shape.StrokeStyle.HasPattern
                ? "the shape already carries a dash, and one stroke can only have one"
            : metrics.IsClosed && !clipped && shape.Fill.Kind is PaintCodePaintKind.Gradient
                ? "a gradient is laid across the wedge's own box, which a circle stroked half its width is not"
                : null;

        if (refusal is { })
        {
            Note(PaintCodeImportSeverity.Dropped, shape.Name, property, refusal + ", so the drawing's own angle is written.");

            return null;
        }

        // The angle that stays put, and the expression that moves.
        var fixedAngle = start ? metrics.EndAngle : metrics.StartAngle;

        // Expression and Produced have already said why, where they answer nothing, and the arc
        // keeps the angle it was saved with.
        if (Expression(shape, property, shape.Name) is not { } written ||
            Produced(shape, property, shape.Name) is not { } produced)
        {
            return null;
        }

        // The difference between what the expression comes to and the angle the drawing was saved
        // at, added back the way a driven transform's is.
        var shift = Number((start ? metrics.StartAngle : metrics.EndAngle) - produced);
        var code = shift == "0" ? written : $"({written}) + {shift}";

        var box = PaintCodePathData.Box(shape);
        var radius = box.Width / 2;

        // A pie of radius r is exactly a circle of radius r/2 stroked r wide: the stroke covers
        // every radius from the middle to the rim, and a butt cap on a circle is perpendicular to
        // the tangent, which is the radial direction -- so the caps are the wedge's straight edges
        // rather than an approximation of them.
        var drawn = metrics.IsClosed ? radius / 2 : radius;
        var circumference = 2 * Math.PI * drawn;

        // PaintCode's own sum, written out: the sweep from the fixed angle to the driven one, and a
        // whole turn for every one the driven end has gone past it. Ceil of nought or less is nought
        // or less, so max(0, ...) is the ternary its generated code writes.
        var sweep = start
            ? $"({code}) - {Number(fixedAngle)} + 360 * max(0, ceil(({Number(fixedAngle)} - ({code})) / 360))"
            : $"{Number(fixedAngle)} - ({code}) + 360 * max(0, ceil((({code}) - {Number(fixedAngle)}) / 360))";

        // What the dash hides, which is the rest of the circle. Clamped for the reason Skia clamps
        // its own sweep: past a whole turn it draws the whole of it, and below nothing it draws
        // nothing.
        var offset = Braces($"clamp(360 - ({sweep}), 0, 360) * {Number(circumference / 360)}");

        return new Sweeping(
            PaintCodePathData.Ring(box.X + radius, box.Y + radius, drawn, fixedAngle, !start),
            circumference,
            offset,
            metrics.IsClosed ? radius : null);
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
