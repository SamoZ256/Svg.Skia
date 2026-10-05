// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Svg.Skia;
using Svg.SourceEditing;
using Shim = ShimSkiaSharp;
using SK = SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// A shape being drawn onto a drawing: the tool that is armed, the points the hand has put down,
/// and the element those come to.
/// </summary>
/// <remarks>
/// <para>
/// Gesture arithmetic in the role <see cref="SvgViewerGizmos"/> and <see cref="SvgViewerSweep"/>
/// play: it never touches the built document and never writes the file. The host maps the pointer
/// into the parent's own space, hands the points over, shows <see cref="Preview"/> as a ring, and
/// commits what <see cref="Finish"/> builds through its own history. Both hosts hold one.
/// </para>
/// <para>
/// Everything is in the parent's user space, which is where the numbers are written. A shape drawn
/// inside a <c>&lt;g transform="scale(2)"&gt;</c> is half the size on paper that it is on screen, and
/// the preview is traced from the same numbers and put back through the same matrix, so what is
/// shown while the hand moves is what the file will say.
/// </para>
/// </remarks>
public sealed class SvgViewerDraw
{
    /// <summary>The tools, as the element each writes; <c>circle</c> is the ellipse tool with Shift held.</summary>
    public static readonly IReadOnlyList<string> Shapes = new[] { "rect", "ellipse", "line", "text" };

    private static readonly IReadOnlyDictionary<string, (string Label, Key Key, string Tip)> Tools =
        new Dictionary<string, (string, Key, string)>(StringComparer.Ordinal)
        {
            ["rect"] = ("Rect", Key.R, "Rectangle (R): drag a box; Shift for a square"),
            ["ellipse"] = ("Ellipse", Key.O, "Ellipse (O): drag a box; Shift for a circle"),
            ["line"] = ("Line", Key.L, "Line (L): drag from one end to the other; Shift for 45° steps"),
            ["text"] = ("Text", Key.T, "Text (T): click where the baseline starts"),
        };

    private string? _shape;
    private Shim.SKMatrix _fromParent = Shim.SKMatrix.CreateIdentity();
    private Shim.SKMatrix _toParent = Shim.SKMatrix.CreateIdentity();
    private double _scale = 1d;
    private SvgElement? _parent;
    private Shim.SKPoint _pressed;
    private Shim.SKPoint _current;
    private bool _square;
    private bool _busy;

    /// <summary>The element the armed tool writes, or null while the hand is selecting.</summary>
    /// <remarks>Setting it drops whatever was being drawn: a shape half made with one tool is not the next tool's.</remarks>
    public string? Shape
    {
        get => _shape;
        set
        {
            if (string.Equals(_shape, value, StringComparison.Ordinal))
            {
                return;
            }

            Cancel();

            _shape = value;

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the tool changes, armed or put away.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a shape is in the making.</summary>
    public bool IsBusy => _busy;

    /// <summary>What the edit that writes the shape is called in the history.</summary>
    public string Label => _shape switch
    {
        "rect" => "draw a rectangle",
        "ellipse" => _square ? "draw a circle" : "draw an ellipse",
        "line" => "draw a line",
        "text" => "add text",
        _ => "draw a shape"
    };

    /// <summary>The cursor a canvas shows while the tool is armed, or null for its own.</summary>
    public Cursor? Cursor => _shape switch
    {
        null => null,
        "text" => new Cursor(StandardCursorType.Ibeam),
        _ => new Cursor(StandardCursorType.Cross)
    };

    /// <summary>
    /// Begins a shape under the pointer, which is at <paramref name="at"/> in the drawing's space.
    /// </summary>
    /// <param name="fromParent">The parent's total transform, which the points go back through and the preview forward.</param>
    /// <param name="scale">The view's scale, for sizes that are chosen on screen.</param>
    /// <returns>The sentence refusing the gesture, or null where it began.</returns>
    public string? Start(Shim.SKMatrix fromParent, Shim.SKPoint at, double scale, SvgElement parent)
    {
        if (_shape is null)
        {
            return null;
        }

        if (!fromParent.TryInvert(out var toParent))
        {
            return Flattened;
        }

        _fromParent = fromParent;
        _toParent = toParent;
        _scale = scale;
        _parent = parent;
        _pressed = _current = toParent.MapPoint(at);
        _square = false;
        _busy = true;

        return null;
    }

    /// <summary>Follows the pointer to <paramref name="at"/>, in the drawing's space.</summary>
    public void Drag(Shim.SKPoint at, KeyModifiers modifiers)
    {
        if (!_busy)
        {
            return;
        }

        _square = modifiers.HasFlag(KeyModifiers.Shift);
        _current = _toParent.MapPoint(at);
    }

    /// <summary>Lets go of the shape in the making, writing nothing.</summary>
    public void Cancel()
    {
        _busy = false;
        _square = false;
    }

    /// <summary>
    /// The shape as the hand has it now, in the drawing's space, or null while there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Traced from the same numbers <see cref="Finish"/> writes and put back through the parent's
    /// transform, so the ring cannot promise what the file will not say.
    /// </remarks>
    public SK.SKPath? Preview()
    {
        if (!_busy || _shape is null || _shape == "text")
        {
            return null;
        }

        var box = Box();

        using var builder = new SK.SKPathBuilder();

        switch (_shape)
        {
            case "rect":
                builder.AddRect(new SK.SKRect(box.Left, box.Top, box.Right, box.Bottom));
                break;
            case "ellipse":
                builder.AddOval(new SK.SKRect(box.Left, box.Top, box.Right, box.Bottom));
                break;
            case "line":
                var (x2, y2) = LineEnd();

                builder.MoveTo(_pressed.X, _pressed.Y);
                builder.LineTo(x2, y2);
                break;
            default:
                return null;
        }

        var path = builder.Detach();
        var placement = new SK.SKMatrix(
            _fromParent.ScaleX, _fromParent.SkewX, _fromParent.TransX,
            _fromParent.SkewY, _fromParent.ScaleY, _fromParent.TransY,
            0f, 0f, 1f);

        path.Transform(placement);

        return path;
    }

    /// <summary>
    /// The element the gesture comes to, in no namespace, or null where it would cover nothing.
    /// </summary>
    /// <remarks>
    /// A click with a box tool writes nothing: a rectangle of no size is not something anybody
    /// meant, and a default size would be a guess at what they did. Text is the exception, since a
    /// click is the whole of its gesture. Done with the shape either way.
    /// </remarks>
    public XElement? Finish()
    {
        if (!_busy || _shape is null || _parent is null)
        {
            return null;
        }

        _busy = false;

        var element = _shape switch
        {
            "rect" => Rect(),
            "ellipse" => Ellipse(),
            "line" => Line(),
            "text" => Text(),
            _ => null
        };

        if (element is { })
        {
            Paint(element, _parent);
        }

        return element;
    }

    /// <summary>What a shape of no size says instead of being written.</summary>
    public string? Note => _shape switch
    {
        "rect" => "Drag to draw a rectangle.",
        "ellipse" => "Drag to draw an ellipse.",
        "line" => "Drag to draw a line.",
        _ => null
    };

    private XElement? Rect()
    {
        var box = Box();

        return box.Width <= 0f || box.Height <= 0f
            ? null
            : new XElement(
                "rect",
                new XAttribute("x", Number(box.Left)),
                new XAttribute("y", Number(box.Top)),
                new XAttribute("width", Number(box.Width)),
                new XAttribute("height", Number(box.Height)));
    }

    private XElement? Ellipse()
    {
        var box = Box();

        if (box.Width <= 0f || box.Height <= 0f)
        {
            return null;
        }

        var cx = Number(box.MidX);
        var cy = Number(box.MidY);

        return _square
            ? new XElement(
                "circle",
                new XAttribute("cx", cx),
                new XAttribute("cy", cy),
                new XAttribute("r", Number(box.Width / 2f)))
            : new XElement(
                "ellipse",
                new XAttribute("cx", cx),
                new XAttribute("cy", cy),
                new XAttribute("rx", Number(box.Width / 2f)),
                new XAttribute("ry", Number(box.Height / 2f)));
    }

    private XElement? Line()
    {
        var (x2, y2) = LineEnd();

        return x2 == _pressed.X && y2 == _pressed.Y
            ? null
            : new XElement(
                "line",
                new XAttribute("x1", Number(_pressed.X)),
                new XAttribute("y1", Number(_pressed.Y)),
                new XAttribute("x2", Number(x2)),
                new XAttribute("y2", Number(y2)));
    }

    /// <remarks>
    /// Sixteen pixels tall on screen wherever the zoom is, written in the parent's units: the size
    /// somebody expects of text they just placed is the size of the text around them, and a parent
    /// that doubles everything wants half the number.
    /// </remarks>
    private XElement Text()
    {
        var unit = Math.Sqrt(Math.Abs(_fromParent.ScaleX * _fromParent.ScaleY - _fromParent.SkewX * _fromParent.SkewY));
        var size = 16d / (_scale * (unit > 0d ? unit : 1d));

        return new XElement(
            "text",
            new XAttribute("x", Number(_pressed.X)),
            new XAttribute("y", Number(_pressed.Y)),
            new XAttribute("font-size", Number((float)size)),
            new XText("Text"));
    }

    /// <summary>The box between the press and the pointer, a square of the longer side with Shift held.</summary>
    /// <remarks>The pressed corner stays put: it is the one the hand chose, and the other is still moving.</remarks>
    private SK.SKRect Box()
    {
        var dx = _current.X - _pressed.X;
        var dy = _current.Y - _pressed.Y;

        if (_square)
        {
            var side = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));

            dx = MathF.CopySign(side, dx);
            dy = MathF.CopySign(side, dy);
        }

        return SK.SKRect.Create(
            MathF.Min(_pressed.X, _pressed.X + dx),
            MathF.Min(_pressed.Y, _pressed.Y + dy),
            MathF.Abs(dx),
            MathF.Abs(dy));
    }

    /// <summary>Where the line ends: under the pointer, or on the nearest of the eight directions with Shift.</summary>
    private (float X, float Y) LineEnd()
    {
        if (!_square)
        {
            return (_current.X, _current.Y);
        }

        var dx = _current.X - _pressed.X;
        var dy = _current.Y - _pressed.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        var angle = MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4f)) * (MathF.PI / 4f);

        return (_pressed.X + length * MathF.Cos(angle), _pressed.Y + length * MathF.Sin(angle));
    }

    /// <summary>
    /// Gives a shape paint only where it would otherwise have none to inherit.
    /// </summary>
    /// <remarks>
    /// Nothing, by default: a shape drawn into a Streamline icon's <c>&lt;g fill="none" stroke=…&gt;</c>
    /// should pick up the group's stroke the way its siblings do, and an explicit black would paint
    /// over that. Only where the parent inherits no paint at all is one written, as
    /// <c>currentColor</c> so that the drawing's colour still decides. A closed shape and text get a
    /// fill where the parent has none and no stroke; an open one gets a stroke where there is none.
    /// </remarks>
    private static void Paint(XElement element, SvgElement parent)
    {
        var fill = parent.Fill;
        var stroke = parent.Stroke;
        var noFill = ReferenceEquals(fill, SvgPaintServer.None);
        var noStroke = stroke is null || ReferenceEquals(stroke, SvgPaintServer.None) || ReferenceEquals(stroke, SvgPaintServer.NotSet);

        if (element.Name.LocalName == "line")
        {
            if (noStroke)
            {
                element.Add(new XAttribute("stroke", "currentColor"));
            }
        }
        else if (noFill && noStroke)
        {
            element.Add(new XAttribute("fill", "currentColor"));
        }
    }

    /// <summary>A length as the file spells one.</summary>
    private static string Number(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- where a shape goes -----------------------------------------------------------------

    /// <summary>
    /// Where a new shape is written, given what is selected: inside a selected container, after a
    /// selected shape, or last in the drawing.
    /// </summary>
    /// <remarks>
    /// Containers are the root, a <c>&lt;g&gt;</c> and an <c>&lt;a&gt;</c>, and only where everything
    /// above them is one too — which keeps a shape out of a <c>&lt;defs&gt;</c>, a clip path, a
    /// symbol or a run of text whose row happened to be picked. The same line
    /// <c>SvgElementEditor</c> draws with <c>Kept</c>, drawn here on the built tree because the
    /// parent's matrix is needed before anything is written.
    /// </remarks>
    public static (string Target, SvgElementDrop Where, SvgElement Parent) Into(SvgDocument built, IReadOnlyList<string> selected)
    {
        for (var index = selected.Count - 1; index >= 0; index--)
        {
            if (SvgElementAddress.Parse(selected[index])?.Resolve(built) is not { } element)
            {
                continue;
            }

            if (Holds(element))
            {
                return (selected[index], SvgElementDrop.Inside, element);
            }

            if (element.Parent is { } parent && Holds(parent))
            {
                return (selected[index], SvgElementDrop.After, parent);
            }
        }

        return (string.Empty, SvgElementDrop.Inside, built);
    }

    private static bool Holds(SvgElement element)
    {
        for (var at = element; at is { }; at = at.Parent)
        {
            if (at is not (SvgFragment or SvgGroup or SvgAnchor))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The matrix from <paramref name="parent"/>'s space to the drawing's, or false where the scene has no node for it.
    /// </summary>
    /// <remarks>
    /// The root has no node of its own under the empty address; the scene's root carries its matrix,
    /// which is where the viewBox and any size the host asked for are applied. A container nobody
    /// drew — <c>display="none"</c>, say — has no node either, and the caller falls back to the root.
    /// </remarks>
    public static bool TryParent(SKSvg svg, SvgElement parent, out Shim.SKMatrix fromParent)
    {
        if (parent is SvgDocument)
        {
            if (svg.TryEnsureRetainedSceneGraph(out var scene) && scene is { })
            {
                fromParent = scene.Root.TotalTransform;

                return true;
            }
        }
        else if (svg.TryGetRetainedSceneNodes(parent, out var nodes) && nodes.Count > 0)
        {
            fromParent = nodes[0].TotalTransform;

            return true;
        }

        fromParent = Shim.SKMatrix.CreateIdentity();

        return false;
    }

    // ---- the palette ------------------------------------------------------------------------

    /// <summary>The tool buttons, one strip for both hosts, following and setting <see cref="Shape"/>.</summary>
    public static Control Palette(SvgViewerDraw draw)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<(string? Shape, RadioButton Button)>();

        Add(null, "Select", "Select (V): pick and move what is drawn");

        foreach (var shape in Shapes)
        {
            var (label, _, tip) = Tools[shape];

            Add(shape, label, tip);
        }

        draw.Changed += (_, _) =>
        {
            foreach (var (shape, button) in buttons)
            {
                button.IsChecked = string.Equals(shape, draw.Shape, StringComparison.Ordinal);
            }
        };

        return strip;

        void Add(string? shape, string label, string tip)
        {
            var button = new RadioButton
            {
                Content = label,
                GroupName = "tools",
                IsChecked = string.Equals(shape, draw.Shape, StringComparison.Ordinal),
                [ToolTip.TipProperty] = tip
            };

            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                {
                    draw.Shape = shape;
                }
            };

            buttons.Add((shape, button));
            strip.Children.Add(button);
        }
    }

    /// <summary>
    /// Answers a key on the canvas: a tool's letter arms it, V or Escape puts the tool away.
    /// </summary>
    /// <returns>Whether the key was this gesture's.</returns>
    public bool Pressed(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.Key is Key.V || e.Key is Key.Escape && _shape is { } && !_busy)
        {
            Shape = null;

            return true;
        }

        foreach (var (shape, tool) in Tools)
        {
            if (tool.Key == e.Key)
            {
                Shape = shape;

                return true;
            }
        }

        return false;
    }

    private const string Flattened =
        "That is drawn flat, so there is no way back from the pointer to where a shape would be written.";
}
