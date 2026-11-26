using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Svg.Highlighting;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;
using Shim = ShimSkiaSharp;

namespace Svg.Studio;

public sealed partial class AssistantTools
{
    /// <summary>Where an element is drawn, in its drawing's own user units, and how it stands to a second.</summary>
    /// <remarks>
    /// The box is the geometry's, as the selection box is, without the stroke. The drawing is loaded as
    /// render loads it, so a tab's unsaved text is what is measured.
    /// </remarks>
    private Task<string> Measure(
        [Description("The element's address key from get_drawing; (root) for the page.")] string element,
        [Description("A second element's address key: also says the gaps between the two and the distance and angle from the first's centre to the second's.")] string? to = null,
        [Description("A drawing's node path from get_project; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            var text = target.Text;
            var read = SvgSourceDocument.Read(text, out _);
            var at = (ProjectNode?)Drawing(node);
            SvgViewerDocument document;

            try
            {
                document = GroupPanel.Load(text, at, at is null ? (target as SvgViewer)?.DocumentPath : null);
            }
            catch (Exception failure)
            {
                return $"{about} cannot be drawn: {failure.Message}";
            }

            using (document)
            {
                // The file's keys, which get_drawing gives, to the built drawing's, which the scene is indexed by.
                var built = SvgSourceElements.Addresses(text, document.Built(text))
                    .GroupBy(pair => pair.Value)
                    .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

                if (!SvgViewerDraw.TryParent(document.Svg, null, out var root) || !root.TryInvert(out var units))
                {
                    return $"{about} has no user units to measure in.";
                }

                var first = Box(element);

                if (first.Box is not { } one)
                {
                    return first.Said;
                }

                var said = $"{first.Said}: {Spanned(one)}.";

                if (string.IsNullOrWhiteSpace(to))
                {
                    return said;
                }

                var second = Box(to);

                if (second.Box is not { } other)
                {
                    return said + "\n" + second.Said;
                }

                var across = Math.Max(other.Left - one.Right, one.Left - other.Right);
                var down = Math.Max(other.Top - one.Bottom, one.Top - other.Bottom);
                var dx = Middle(other.Left, other.Right) - Middle(one.Left, one.Right);
                var dy = Middle(other.Top, other.Bottom) - Middle(one.Top, one.Bottom);

                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{said}\n{second.Said}: {Spanned(other)}.\nBetween them: a gap of {across:0.##} across and {down:0.##} down, negative where they overlap; the centres are {Math.Sqrt(dx * dx + dy * dy):0.##} apart at {Math.Atan2(dy, dx) * 180d / Math.PI:0.##}°, clockwise from the x axis.");

                (Shim.SKRect? Box, string Said) Box(string key)
                {
                    var file = Key(key);

                    if (file.Length == 0)
                    {
                        return document.Svg.Picture?.CullRect is { } page
                            ? (units.MapRect(new Shim.SKRect(page.Left, page.Top, page.Right, page.Bottom)), "(root), the page")
                            : (null, "The page has no size to measure.");
                    }

                    if (read is null || SvgAttributeEditor.ElementName(read, file) is not { } tag || !built.TryGetValue(file, out var drawnAt))
                    {
                        return (null, $"There is no element {file}. get_drawing lists the keys.");
                    }

                    var name = $"<{tag}> at {file}";

                    var drawn = document.Svg.TryGetRetainedSceneNodes(drawnAt, out var nodes)
                        ? nodes.Where(SvgViewerOutline.Drawn).ToList()
                        : new List<Svg.Skia.SvgSceneNode>();

                    if (drawn.Count == 0)
                    {
                        return (null, $"{name} draws nothing.");
                    }

                    var box = drawn
                        .Select(found => found.TransformedBounds)
                        .Aggregate((sum, next) => new Shim.SKRect(
                            Math.Min(sum.Left, next.Left),
                            Math.Min(sum.Top, next.Top),
                            Math.Max(sum.Right, next.Right),
                            Math.Max(sum.Bottom, next.Bottom)));

                    return (units.MapRect(box), drawn.Count == 1 ? name : $"{name}, drawn {drawn.Count} times and spanning every copy");
                }
            }
        });

    private static string Spanned(Shim.SKRect box)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"x {box.Left:0.##} to {box.Right:0.##}, y {box.Top:0.##} to {box.Bottom:0.##}; {box.Right - box.Left:0.##} wide and {box.Bottom - box.Top:0.##} tall, centred on {Middle(box.Left, box.Right):0.##}, {Middle(box.Top, box.Bottom):0.##}");

    private static double Middle(float low, float high) => ((double)low + high) / 2d;
}
