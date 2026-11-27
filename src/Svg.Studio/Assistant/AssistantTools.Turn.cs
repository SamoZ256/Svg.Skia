using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Svg.Highlighting;
using Svg.Skia;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

public sealed partial class AssistantTools
{
    private Task<string> Flip(
        [Description("The address keys from get_drawing of the elements to mirror, across the middle of them all.")] string[] keys,
        [Description("True to mirror left to right, false to mirror top to bottom.")] bool horizontal,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Turned(keys, summary, node, (bound, elements) => SvgViewerCommands.Flip(bound, elements, horizontal));

    private Task<string> Rotate(
        [Description("The address keys from get_drawing of the elements to turn, about the middle of them all.")] string[] keys,
        [Description("How far to turn them, in degrees clockwise: 90 or -90 for a quarter turn.")] float degrees,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Turned(keys, summary, node, (bound, elements) => SvgViewerCommands.Rotate(bound, elements, degrees));

    /// <summary>A command worked out on the drawing as render draws it, and written as one undo step.</summary>
    /// <remarks>Loaded as measure loads it, so a tab's unsaved text is what the middle is found in.</remarks>
    private Task<string> Turned(
        string[] keys,
        string summary,
        string? node,
        Func<SKSvg, IReadOnlyList<(string File, string Built)>, Func<SvgDomEditContext, string?>> command)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            var text = target.Text;
            var at = (ProjectNode?)Drawing(node);
            Func<SvgDomEditContext, string?> edit;

            try
            {
                using var document = GroupPanel.Load(text, at, at is null ? (target as SvgViewer)?.DocumentPath : null);

                // The file's keys, which get_drawing gives, to the built drawing's, which the scene is indexed by.
                var built = SvgSourceElements.Addresses(text, document.Built(text))
                    .GroupBy(pair => pair.Value)
                    .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
                var files = keys.Select(Key).ToList();

                if (files.Count == 0)
                {
                    return "Name the elements to turn.";
                }

                if (files.FirstOrDefault(file => !built.ContainsKey(file)) is { } missing)
                {
                    return $"There is no element {missing}. get_drawing lists the keys.";
                }

                edit = command(document.Svg, files.Select(file => (file, built[file])).ToList());
            }
            catch (Exception failure)
            {
                return $"{about} cannot be drawn: {failure.Message}";
            }

            string? note = null;

            return Edit(node, summary, source => SvgDomEdit.Apply(source, edit, out _, out note), () => note);
        });
}
