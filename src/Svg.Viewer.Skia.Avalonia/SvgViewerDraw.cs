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
/// into the drawing's space, hands the points over, shows <see cref="Preview"/> as a ring, and
/// commits what <see cref="Finish"/> builds through its own history. Both hosts hold one.
/// </para>
/// <para>
/// Two kinds of tool. A box tool — rect, ellipse, line, text — is one press to one release, and the
/// release is the shape. A point tool — polygon, polyline, path — <see cref="Continues"/> past the
/// release: each click or drag puts down a point, and the shape is done when the first or last
/// point is clicked again or Enter is pressed. The host tells the two apart by that one property.
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
    public static readonly IReadOnlyList<string> Shapes = new[] { "rect", "ellipse", "line", "text", "polygon", "polyline", "path" };

    private static readonly IReadOnlyDictionary<string, (string Label, Key Key, string Tip)> Tools =
        new Dictionary<string, (string, Key, string)>(StringComparer.Ordinal)
        {
            ["rect"] = ("Rect", Key.R, "Rectangle (R): drag a box; Shift for a square"),
            ["ellipse"] = ("Ellipse", Key.O, "Ellipse (O): drag a box; Shift for a circle"),
            ["line"] = ("Line", Key.L, "Line (L): drag from one end to the other; Shift for 45° steps"),
            ["text"] = ("Text", Key.T, "Text (T): click where the baseline starts"),
            ["polygon"] = ("Polygon", Key.None, "Polygon: click each corner; click the first again or press Enter to finish, Backspace to take one back"),
            ["polyline"] = ("Polyline", Key.None, "Polyline: click each point; click the last again or press Enter to finish, Backspace to take one back"),
            ["path"] = ("Pen", Key.P, "Pen (P): click for a corner, drag for a curve; click the first point to close, the last or Enter to finish"),
        };

    /// <summary>How near a click has to come to a point to mean that point, on screen.</summary>
    private const double NearPixels = 6d;

    private string? _shape;
    private Shim.SKMatrix _fromParent = Shim.SKMatrix.CreateIdentity();
    private Shim.SKMatrix _toParent = Shim.SKMatrix.CreateIdentity();
    private double _scale = 1d;
    private float _unit = 1f;
    private SvgElement? _parent;
    private Shim.SKPoint _pressed;
    private Shim.SKPoint _current;
    private Shim.SKPoint? _hover;
    private bool _square;
    private bool _busy;
    private bool _pending;
    private bool _complete;
    private bool _closed;

    /// <summary>The points put down so far, each with the handle a drag gave it, for a point tool.</summary>
    /// <remarks>The handle is the point the drag ended at; the one going into the point is its mirror.</remarks>
    private readonly List<(Shim.SKPoint At, Shim.SKPoint? Handle)> _points = new();

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

    /// <summary>Whether the armed tool puts down points past the release, rather than being done by it.</summary>
    public bool Continues => _shape is "polygon" or "polyline" or "path";

    /// <summary>Whether a point tool's shape has been finished, and is ready for <see cref="Finish"/>.</summary>
    public bool IsComplete => _complete;

    /// <summary>What the edit that writes the shape is called in the history.</summary>
    public string Label => _shape switch
    {
        "rect" => "draw a rectangle",
        "ellipse" => _square ? "draw a circle" : "draw an ellipse",
        "line" => "draw a line",
        "text" => "add text",
        "polygon" => "draw a polygon",
        "polyline" => "draw a polyline",
        "path" => "draw a path",
        _ => "draw a shape"
    };

    /// <summary>The cursor a canvas shows while the tool is armed, or null for its own.</summary>
    public Cursor? Cursor => _shape switch
    {
        null => null,
        "text" => new Cursor(StandardCursorType.Ibeam),
        _ => new Cursor(StandardCursorType.Cross)
    };

    /// <summary>What a shape the tool will not write says instead: how the tool is used.</summary>
    public string? Note => _shape switch
    {
        "rect" => "Drag to draw a rectangle.",
        "ellipse" => "Drag to draw an ellipse.",
        "line" => "Drag to draw a line.",
        "polygon" => "A polygon needs three corners: click each, then click the first again or press Enter.",
        "polyline" => "A polyline needs two points: click each, then click the last again or press Enter.",
        "path" => "A path needs two points: click for a corner or drag for a curve, then click the last again or press Enter.",
        _ => null
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
        _square = false;
        _busy = true;
        _complete = false;
        _closed = false;
        _points.Clear();

        // How long a unit of the parent is on screen, so a size chosen there can be written here.
        var area = Math.Abs(fromParent.ScaleX * fromParent.ScaleY - fromParent.SkewX * fromParent.SkewY);

        _unit = area > 0d ? (float)Math.Sqrt(area) : 1f;

        Press(at);

        return null;
    }

    /// <summary>A press while a point tool's shape is in the making: the next point, until it is let go of.</summary>
    public void Press(Shim.SKPoint at)
    {
        if (!_busy)
        {
            return;
        }

        _pressed = _current = _toParent.MapPoint(at);
        _hover = null;
        _pending = true;
    }

    /// <summary>Follows the pointer to <paramref name="at"/>, in the drawing's space, while it is held.</summary>
    public void Drag(Shim.SKPoint at, KeyModifiers modifiers)
    {
        if (!_busy)
        {
            return;
        }

        _square = modifiers.HasFlag(KeyModifiers.Shift);
        _current = _toParent.MapPoint(at);
    }

    /// <summary>
    /// The pointer let go of after travelling. A box tool's shape is then ready for
    /// <see cref="Finish"/>; a point tool puts the point down — where the drag ended for a polygon
    /// or a polyline, and where it began for the pen, with the drag as the curve's handle.
    /// </summary>
    public void Release(Shim.SKPoint at)
    {
        if (!_busy || !_pending)
        {
            return;
        }

        _current = _toParent.MapPoint(at);
        _pending = false;

        switch (_shape)
        {
            case "polygon" or "polyline":
                _points.Add((Constrained(_current), null));
                break;
            case "path":
                _points.Add((_pressed, _current));
                break;
        }
    }

    /// <summary>Follows the pointer between presses, for the rubber band a point tool shows.</summary>
    public void Hover(Shim.SKPoint at)
    {
        if (_busy && Continues)
        {
            _hover = _toParent.MapPoint(at);
        }
    }

    /// <summary>
    /// A click with a point tool: the next point, or the end of the shape where it lands on the
    /// first or the last point put down.
    /// </summary>
    /// <remarks>
    /// The last point, because that is what a double click is to the canvas, which does not count
    /// clicks for a host; and the first because closing a shape is reaching back to where it began.
    /// A polyline closed that way gains its first point at the end, since it has no other way to
    /// spell being closed. Fewer points than the shape needs is not an end but another point.
    /// </remarks>
    /// <returns>Whether the shape is finished, and ready for <see cref="Finish"/>.</returns>
    public bool Click(Shim.SKPoint at)
    {
        if (!_busy || !Continues)
        {
            return false;
        }

        var point = _toParent.MapPoint(at);
        var near = (float)(NearPixels / (_scale * _unit));

        _pending = false;
        _hover = null;

        if (_points.Count >= Minimum && Near(_points[^1].At, point, near))
        {
            _complete = true;
        }
        else if (_points.Count >= Minimum && Near(_points[0].At, point, near))
        {
            _closed = true;
            _complete = true;
        }
        else
        {
            _points.Add((Constrained(point), null));
        }

        return _complete;
    }

    /// <summary>Lets go of the press in flight, keeping the points already put down.</summary>
    /// <remarks>What a click does on its way to <see cref="Click"/>, and what Escape does mid-drag.</remarks>
    public void Drop()
    {
        _pending = false;

        if (!Continues)
        {
            Cancel();
        }
    }

    /// <summary>Lets go of the shape in the making, writing nothing.</summary>
    public void Cancel()
    {
        _busy = false;
        _pending = false;
        _complete = false;
        _closed = false;
        _square = false;
        _hover = null;
        _points.Clear();
    }

    /// <summary>
    /// The shape as the hand has it now, in the drawing's space, or null while there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Traced from the same numbers <see cref="Finish"/> writes and put back through the parent's
    /// transform, so the ring cannot promise what the file will not say. A point tool shows the
    /// points put down, the one being put down, and a band to the pointer.
    /// </remarks>
    public SK.SKPath? Preview()
    {
        if (!_busy || _shape is null || _shape == "text")
        {
            return null;
        }

        using var builder = new SK.SKPathBuilder();

        switch (_shape)
        {
            case "rect":
                var box = Box();

                builder.AddRect(new SK.SKRect(box.Left, box.Top, box.Right, box.Bottom));
                break;
            case "ellipse":
                var oval = Box();

                builder.AddOval(new SK.SKRect(oval.Left, oval.Top, oval.Right, oval.Bottom));
                break;
            case "line":
                var (x2, y2) = LineEnd();

                builder.MoveTo(_pressed.X, _pressed.Y);
                builder.LineTo(x2, y2);
                break;
            case "polygon" or "polyline":
                Points(builder, Showing(), close: _shape == "polygon");
                break;
            case "path":
                Curve(builder, Showing(), close: false);
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

    /// <summary>The points as they are now shown: those put down, then the one being put down or the pointer.</summary>
    private IReadOnlyList<(Shim.SKPoint At, Shim.SKPoint? Handle)> Showing()
    {
        var shown = new List<(Shim.SKPoint, Shim.SKPoint?)>(_points);

        if (_pending)
        {
            shown.Add(_shape == "path" ? (_pressed, _current) : (Constrained(_current), null));
        }
        else if (_hover is { } hover)
        {
            shown.Add((Constrained(hover), null));
        }

        return shown;
    }

    /// <summary>
    /// The element the gesture comes to, in no namespace, or null where it would cover nothing.
    /// </summary>
    /// <remarks>
    /// A click with a box tool writes nothing: a rectangle of no size is not something anybody
    /// meant, and a default size would be a guess at what they did. Text is the exception, since a
    /// click is the whole of its gesture. A point tool with too few points is the same case. Done
    /// with the shape either way.
    /// </remarks>
    public XElement? Finish()
    {
        if (!_busy || _shape is null || _parent is null)
        {
            return null;
        }

        var element = _shape switch
        {
            "rect" => Rect(),
            "ellipse" => Ellipse(),
            "line" => Line(),
            "text" => Text(),
            "polygon" or "polyline" => Poly(),
            "path" => Path(),
            _ => null
        };

        if (element is { })
        {
            Paint(element, _parent, open: _shape is "line" or "polyline" || _shape == "path" && !_closed);
        }

        Cancel();

        return element;
    }

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
        => new(
            "text",
            new XAttribute("x", Number(_pressed.X)),
            new XAttribute("y", Number(_pressed.Y)),
            new XAttribute("font-size", Number((float)(16d / (_scale * _unit)))),
            new XText("Text"));

    private XElement? Poly()
    {
        if (_points.Count < Minimum)
        {
            return null;
        }

        var points = _points.Select(point => point.At).ToList();

        // Closed by reaching back to the first point, which a polyline can only spell by ending there.
        if (_closed && _shape == "polyline")
        {
            points.Add(points[0]);
        }

        return new XElement(
            _shape!,
            new XAttribute("points", string.Join(" ", points.Select(point => Number(point.X) + "," + Number(point.Y)))));
    }

    private XElement? Path()
    {
        if (_points.Count < Minimum)
        {
            return null;
        }

        var data = new StringBuilder();

        data.Append("M ").Append(Number(_points[0].At.X)).Append(' ').Append(Number(_points[0].At.Y));

        for (var index = 1; index < _points.Count; index++)
        {
            Segment(data, _points[index - 1], _points[index]);
        }

        if (_closed)
        {
            Segment(data, _points[^1], _points[0]);
            data.Append(" Z");
        }

        return new XElement("path", new XAttribute("d", data.ToString()));
    }

    /// <summary>One segment of the path: a curve where either end has a handle, and a line otherwise.</summary>
    /// <remarks>
    /// A handle is the point the drag ended at and leads <em>out</em> of its anchor; the one leading
    /// in is its mirror, so a dragged anchor is smooth. An end with no handle puts its control point
    /// on itself, which is how a curve is written straight at that end.
    /// </remarks>
    private static void Segment(StringBuilder data, (Shim.SKPoint At, Shim.SKPoint? Handle) from, (Shim.SKPoint At, Shim.SKPoint? Handle) to)
    {
        if (from.Handle is null && to.Handle is null)
        {
            data.Append(" L ").Append(Number(to.At.X)).Append(' ').Append(Number(to.At.Y));

            return;
        }

        var leaving = from.Handle ?? from.At;
        var arriving = to.Handle is { } handle ? new Shim.SKPoint(2f * to.At.X - handle.X, 2f * to.At.Y - handle.Y) : to.At;

        data.Append(" C ")
            .Append(Number(leaving.X)).Append(' ').Append(Number(leaving.Y)).Append(' ')
            .Append(Number(arriving.X)).Append(' ').Append(Number(arriving.Y)).Append(' ')
            .Append(Number(to.At.X)).Append(' ').Append(Number(to.At.Y));
    }

    private static void Points(SK.SKPathBuilder builder, IReadOnlyList<(Shim.SKPoint At, Shim.SKPoint? Handle)> points, bool close)
    {
        if (points.Count == 0)
        {
            return;
        }

        builder.MoveTo(points[0].At.X, points[0].At.Y);

        for (var index = 1; index < points.Count; index++)
        {
            builder.LineTo(points[index].At.X, points[index].At.Y);
        }

        // The closing edge only once there is a shape to close, or a band to the pointer reads as two.
        if (close && points.Count > 2)
        {
            builder.Close();
        }
    }

    private static void Curve(SK.SKPathBuilder builder, IReadOnlyList<(Shim.SKPoint At, Shim.SKPoint? Handle)> points, bool close)
    {
        if (points.Count == 0)
        {
            return;
        }

        builder.MoveTo(points[0].At.X, points[0].At.Y);

        for (var index = 1; index < points.Count; index++)
        {
            Curve(builder, points[index - 1], points[index]);
        }

        if (close && points.Count > 1)
        {
            Curve(builder, points[^1], points[0]);
            builder.Close();
        }
    }

    private static void Curve(SK.SKPathBuilder builder, (Shim.SKPoint At, Shim.SKPoint? Handle) from, (Shim.SKPoint At, Shim.SKPoint? Handle) to)
    {
        if (from.Handle is null && to.Handle is null)
        {
            builder.LineTo(to.At.X, to.At.Y);

            return;
        }

        var leaving = from.Handle ?? from.At;
        var arriving = to.Handle is { } handle ? new Shim.SKPoint(2f * to.At.X - handle.X, 2f * to.At.Y - handle.Y) : to.At;

        builder.CubicTo(leaving.X, leaving.Y, arriving.X, arriving.Y, to.At.X, to.At.Y);
    }

    /// <summary>How many points the armed point tool needs before it is a shape.</summary>
    private int Minimum => _shape == "polygon" ? 3 : 2;

    private static bool Near(Shim.SKPoint a, Shim.SKPoint b, float within)
        => MathF.Abs(a.X - b.X) <= within && MathF.Abs(a.Y - b.Y) <= within;

    /// <summary>The point, held to one of the eight directions from the last point while Shift is down.</summary>
    private Shim.SKPoint Constrained(Shim.SKPoint point)
    {
        if (!_square || _points.Count == 0)
        {
            return point;
        }

        var (x, y) = Eighth(_points[^1].At, point);

        return new Shim.SKPoint(x, y);
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
        => _square ? Eighth(_pressed, _current) : (_current.X, _current.Y);

    /// <summary>The point as far from <paramref name="from"/> as it is, on the nearest of the eight directions.</summary>
    private static (float X, float Y) Eighth(Shim.SKPoint from, Shim.SKPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        var angle = MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4f)) * (MathF.PI / 4f);

        return (from.X + length * MathF.Cos(angle), from.Y + length * MathF.Sin(angle));
    }

    /// <summary>
    /// Gives a shape paint only where it would otherwise have none to inherit.
    /// </summary>
    /// <remarks>
    /// Nothing, by default: a shape drawn into a Streamline icon's <c>&lt;g fill="none" stroke=…&gt;</c>
    /// should pick up the group's stroke the way its siblings do, and an explicit black would paint
    /// over that. Only where the parent inherits no paint at all is one written, as
    /// <c>currentColor</c> so that the drawing's colour still decides. A closed shape and text get a
    /// fill where the parent has none and no stroke; an open one gets a stroke where there is none —
    /// and, unless the parent already has none, no fill, since the default black would fill the gap
    /// between its ends.
    /// </remarks>
    private static void Paint(XElement element, SvgElement parent, bool open)
    {
        var fill = parent.Fill;
        var stroke = parent.Stroke;
        var noFill = ReferenceEquals(fill, SvgPaintServer.None);
        var noStroke = stroke is null || ReferenceEquals(stroke, SvgPaintServer.None) || ReferenceEquals(stroke, SvgPaintServer.NotSet);

        if (open)
        {
            if (noStroke)
            {
                element.Add(new XAttribute("stroke", "currentColor"));
            }

            if (!noFill && element.Name.LocalName != "line")
            {
                element.Add(new XAttribute("fill", "none"));
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
    /// Answers a key on the canvas: a tool's letter arms it, V or Escape puts the tool away, and
    /// while a point tool has a shape in the making Enter finishes it, Backspace takes the last
    /// point back and Escape drops the lot.
    /// </summary>
    /// <returns>Whether the key was this gesture's; the host then asks <see cref="IsComplete"/>.</returns>
    public bool Pressed(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (_busy && Continues)
        {
            switch (e.Key)
            {
                case Key.Enter or Key.Return:
                    _pending = false;
                    _complete = _points.Count >= Minimum;

                    return true;
                case Key.Back:
                    if (_points.Count > 0)
                    {
                        _points.RemoveAt(_points.Count - 1);
                    }

                    return true;
                case Key.Escape:
                    Cancel();

                    return true;
            }
        }

        if (e.Key is Key.V || e.Key is Key.Escape && _shape is { } && !_busy)
        {
            Shape = null;

            return true;
        }

        foreach (var (shape, tool) in Tools)
        {
            if (tool.Key != Key.None && tool.Key == e.Key)
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
