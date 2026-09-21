using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Skia;
using Xunit;
using Shim = ShimSkiaSharp;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// One box round several elements, and one gesture carried into each of their own frames.
/// </summary>
/// <remarks>
/// Against the type rather than through a pointer: nothing wires a selection of several yet, and
/// these are the contract the half that will wire it is written against. The numbers are exact —
/// both shapes are twenty across and the box round them spans 20..80, so every factor is a whole
/// one and a bug in the conjugation cannot hide behind a rounding.
/// </remarks>
public class SvgViewerGizmosTests
{
    private const string Pair = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    /// <summary>The same pair, with the second one a shape that can hold a turn in its own numbers.</summary>
    private const string Mixed = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <polygon id="two" points="60,60 80,60 80,80" fill="#cc3366" />
        </svg>
        """;

    /// <summary>One shape that draws, and one tucked away in defs that does not.</summary>
    private const string Hidden = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <defs><rect id="away" x="60" y="60" width="20" height="20" /></defs>
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    /// <summary>A shape beside one drawn so flat there is no way back from the pointer to it.</summary>
    private const string Flattened = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" transform="translate(1, 1)" fill="#3366cc" />
          <rect id="flat" x="60" y="60" width="20" height="20" transform="scale(0)" fill="#cc3366" />
        </svg>
        """;

    private static SKSvg Drawn(string markup)
    {
        var svg = new SKSvg();

        svg.FromSvg(markup);

        return svg;
    }

    private static SvgViewerGizmos Tracking(SKSvg svg, params string[] ids)
    {
        var gizmos = new SvgViewerGizmos();

        gizmos.Track(
            svg,
            default,
            ids.Select(id => new SvgViewerGizmoMember(svg.SourceDocument!.GetElementById(id), id)).ToList());

        return gizmos;
    }

    /// <summary>What one member of a finished gesture wants written, as one string.</summary>
    private static string Wrote(SvgViewerEdits? edits, string key)
    {
        Assert.NotNull(edits);

        var member = edits!.Value.Members.FirstOrDefault(one => one.Key == key);

        return member.Writes is null
            ? "nothing"
            : string.Join(" ", member.Writes.Select(write => $"{write.Name}={write.Value}"));
    }

    private static SvgViewerEdits? Drag(SvgViewerGizmos gizmos, (float X, float Y) from, (float X, float Y) to)
    {
        Assert.Null(gizmos.Begin(new Shim.SKPoint(from.X, from.Y), 1f));

        gizmos.Drag(new Shim.SKPoint(to.X, to.Y));

        return gizmos.End();
    }

    [Fact]
    public void The_Box_Spans_Every_Member()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");
        var box = gizmos.Box(1f);

        Assert.NotNull(box);
        Assert.Equal(20f, box!.Value.TL.X);
        Assert.Equal(20f, box.Value.TL.Y);
        Assert.Equal(80f, box.Value.BR.X);
        Assert.Equal(80f, box.Value.BR.Y);
        Assert.Equal(50f, box.Value.Center.X);
    }

    /// <summary>A member asked for that is not there leaves the rest with their handles.</summary>
    /// <remarks>
    /// A row can name something that does not draw. Counting what was asked for rather than what
    /// was found once left a selection of two with one survivor holding nothing at all — no box, no
    /// handles, and no drag — because everything was delegating to a gizmo tracked with nothing.
    /// </remarks>
    [Fact]
    public void A_Member_That_Is_Not_There_Leaves_The_Rest_With_Their_Handles()
    {
        using var svg = Drawn(Hidden);
        var gizmos = Tracking(svg, "one", "away");
        var box = gizmos.Box(1f);

        Assert.NotNull(box);
        Assert.Equal(20f, box!.Value.TL.X);
        Assert.Equal(40f, box.Value.BR.X);

        Assert.Equal("x=40 y=30", Wrote(Drag(gizmos, (30f, 30f), (50f, 40f)), "one"));
    }

    /// <summary>
    /// A press one member refuses leaves every member's transform exactly as it was.
    /// </summary>
    /// <remarks>
    /// A press is all or none, so a refusal puts everything back — and "back" for a member the
    /// press never reached is where it already is. Putting an empty collection there instead took
    /// the transform off every shape in the selection, and the next drag wrote the loss to the file.
    /// </remarks>
    [Fact]
    public void A_Press_One_Member_Refuses_Leaves_Every_Transform_Alone()
    {
        using var svg = Drawn(Flattened);
        var gizmos = Tracking(svg, "one", "flat");

        var one = svg.SourceDocument!.GetElementById("one");
        var flat = svg.SourceDocument.GetElementById("flat");

        var before = (One: one.Transforms?.ToString(), Flat: flat.Transforms?.ToString());

        Assert.NotNull(gizmos.Begin(new Shim.SKPoint(30f, 30f), 1f));

        Assert.Equal(before.One, one.Transforms?.ToString());
        Assert.Equal(before.Flat, flat.Transforms?.ToString());
    }

    /// <summary>
    /// The inside of the box belongs to nobody where no member is drawn there.
    /// </summary>
    /// <remarks>
    /// A box round two shapes at opposite corners covers a great deal of canvas. Claiming all of it
    /// would take the press that draws the next rectangle.
    /// </remarks>
    [Fact]
    public void A_Press_On_Bare_Canvas_Inside_The_Box_Is_Not_The_Gizmos()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        Assert.False(gizmos.Hits(new Shim.SKPoint(50f, 50f), 1f));
        Assert.True(gizmos.Hits(new Shim.SKPoint(30f, 30f), 1f));
        Assert.True(gizmos.Hits(new Shim.SKPoint(80f, 80f), 1f));
    }

    [Fact]
    public void Dragging_The_Body_Moves_Every_Member()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        var edits = Drag(gizmos, (30f, 30f), (50f, 40f));

        Assert.Equal("x=40 y=30", Wrote(edits, "one"));
        Assert.Equal("x=80 y=70", Wrote(edits, "two"));
    }

    /// <summary>
    /// Every member follows the pointer while the drag runs, not only at the drop.
    /// </summary>
    /// <remarks>
    /// Read off the scene before the gesture ends, which is the only place the difference shows: a
    /// release rebuilds the drawing from its text and puts everything right, so a drag that moved
    /// one shape at a time looked correct the moment anybody let go of it.
    /// </remarks>
    [Fact]
    public void Every_Member_Is_Redrawn_While_The_Drag_Runs()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        Assert.Null(gizmos.Begin(new Shim.SKPoint(30f, 30f), 1f));

        gizmos.Drag(new Shim.SKPoint(50f, 40f));

        // The first member, which is not the one that carries the render.
        var one = svg.SourceDocument!.GetElementById("one");

        Assert.True(svg.TryGetRetainedSceneNodes(one, out var nodes) && nodes.Count > 0);
        Assert.Equal(40f, nodes[0].TransformedBounds.Left, 3);

        gizmos.Cancel();
    }

    /// <summary>A corner scales everything about the corner of the box opposite it.</summary>
    [Fact]
    public void A_Corner_Scales_Every_Member_About_The_Shared_Corner()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        // The bottom right of a box spanning 20..80, pulled out to 140: twice the size about 20,20.
        var edits = Drag(gizmos, (80f, 80f), (140f, 140f));

        Assert.Equal("x=20 y=20 width=40 height=40", Wrote(edits, "one"));
        Assert.Equal("x=100 y=100 width=40 height=40", Wrote(edits, "two"));
    }

    /// <summary>A side handle stretches one axis of every member, and leaves the other alone.</summary>
    [Fact]
    public void A_Side_Handle_Scales_One_Axis_For_Every_Member()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        var edits = Drag(gizmos, (80f, 50f), (140f, 50f));

        Assert.Equal("x=20 y=20 width=40 height=20", Wrote(edits, "one"));
        Assert.Equal("x=100 y=60 width=40 height=20", Wrote(edits, "two"));
    }

    /// <summary>
    /// The stalk turns everything about the middle of the box, not about each shape's own middle.
    /// </summary>
    /// <remarks>
    /// Which is what keeps the selection's arrangement: turned about its own centre each shape would
    /// spin where it stands, and the set would come apart.
    /// </remarks>
    [Fact]
    public void The_Stalk_Turns_Every_Member_About_The_Shared_Centre()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        // The stalk stands twenty above the top middle, and a quarter turn takes it to the right.
        var edits = Drag(gizmos, (50f, 0f), (100f, 50f));

        Assert.Equal("transform=rotate(90, 50, 50)", Wrote(edits, "one"));
        Assert.Equal("transform=rotate(90, 50, 50)", Wrote(edits, "two"));
    }

    /// <summary>
    /// Each member answers the same gesture with whatever it can hold.
    /// </summary>
    /// <remarks>
    /// A rect has no attribute for an angle and takes the turn as a transform; a polygon has points
    /// and takes it in them. One release, two different kinds of answer, one commit.
    /// </remarks>
    [Fact]
    public void Every_Member_Answers_With_What_It_Can_Hold()
    {
        using var svg = Drawn(Mixed);
        var gizmos = Tracking(svg, "one", "two");

        var edits = Drag(gizmos, (50f, 0f), (100f, 50f));

        Assert.Equal("transform=rotate(90, 50, 50)", Wrote(edits, "one"));
        Assert.Equal("points=40,60 40,80 20,80", Wrote(edits, "two"));
    }

    /// <summary>A gesture that came to nothing is not an edit, and writes nothing.</summary>
    [Fact]
    public void A_Gesture_That_Moved_Nothing_Is_Not_An_Edit()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        Assert.Null(Drag(gizmos, (30f, 30f), (30f, 30f)));
    }

    /// <summary>Letting go puts every member back, not only the one the pointer was over.</summary>
    [Fact]
    public void Cancelling_Puts_Every_Member_Back()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one", "two");

        Assert.Null(gizmos.Begin(new Shim.SKPoint(30f, 30f), 1f));

        gizmos.Drag(new Shim.SKPoint(50f, 40f));
        gizmos.Cancel();

        var one = (SvgRectangle)svg.SourceDocument!.GetElementById("one");
        var two = (SvgRectangle)svg.SourceDocument.GetElementById("two");

        Assert.Equal(20f, one.X.Value);
        Assert.Equal(60f, two.X.Value);
        Assert.False(gizmos.IsDragging);
    }

    /// <summary>A selection of one is the single gizmo, down to what it spells.</summary>
    [Fact]
    public void A_Selection_Of_One_Writes_What_It_Always_Wrote()
    {
        using var svg = Drawn(Pair);
        var gizmos = Tracking(svg, "one");

        var edits = Drag(gizmos, (30f, 30f), (50f, 40f));

        Assert.Equal("x=40 y=30", Wrote(edits, "one"));
        Assert.Equal("move an element", edits!.Value.Label);
    }

    /// <summary>
    /// A drawing put elsewhere on a board is dragged where it sits, and written where it is.
    /// </summary>
    /// <remarks>
    /// Everything a host says and hears is in the space the drawings are arranged in; everything
    /// inside is in the drawing's own. Here the two are two hundred apart, so a box at 220 and a
    /// press at 230 are answers about a shape whose own numbers say 20 and 30.
    /// </remarks>
    [Fact]
    public void A_Drawing_Put_Elsewhere_Is_Dragged_Where_It_Sits()
    {
        using var svg = Drawn(Pair);

        var gizmos = new SvgViewerGizmos();

        gizmos.Track(
            svg,
            new Shim.SKPoint(200f, 0f),
            new[]
            {
                new SvgViewerGizmoMember(svg.SourceDocument!.GetElementById("one"), "one"),
                new SvgViewerGizmoMember(svg.SourceDocument.GetElementById("two"), "two")
            });

        var box = gizmos.Box(1f);

        Assert.NotNull(box);
        Assert.Equal(220f, box!.Value.TL.X);
        Assert.Equal(280f, box.Value.BR.X);

        // Pressed over the first shape, two hundred along from where the drawing says it is.
        var edits = Drag(gizmos, (230f, 30f), (250f, 40f));

        Assert.Equal("x=40 y=30", Wrote(edits, "one"));
        Assert.Equal("x=80 y=70", Wrote(edits, "two"));
    }
}
