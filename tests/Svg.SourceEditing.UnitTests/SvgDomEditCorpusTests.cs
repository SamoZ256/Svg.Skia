using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Svg;
using Svg.Transforms;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Commands run over every drawing in the suites, each checked by the edit's own reading back: what
/// is written reads as what the command made, or the edit is refused, never written wrong.
/// </summary>
public class SvgDomEditCorpusTests
{
    private sealed record Tally(int Written, int Unchanged, Dictionary<string, int> Refused, List<string> Differ, TimeSpan Took);

    private static Tally Over(Func<SvgDomEditContext, string?> command)
    {
        var (written, unchanged) = (0, 0);
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);
        var differ = new List<string>();
        var clock = Stopwatch.StartNew();

        foreach (var file in SvgSourceDocumentTests.Suites())
        {
            var text = File.ReadAllText(file);

            if (SvgSourceDocument.Read(text, out _) is not { } source)
            {
                continue;
            }

            var before = source.ToText();
            var refusal = SvgDomEdit.Apply(source, command, out _, out _);

            if (refusal is null)
            {
                if (source.ToText() == before)
                {
                    unchanged++;
                }
                else
                {
                    written++;
                }

                continue;
            }

            // A refusal names its element and attribute; the kind is the sentence around them.
            var kind = refusal.StartsWith("This drawing does not read as SVG", StringComparison.Ordinal) ? "unreadable" : refusal.Length > 90 ? refusal.Substring(0, 90) : refusal;

            refused[kind] = refused.TryGetValue(kind, out var count) ? count + 1 : 1;

            // Only a drawing whose sheet the edit overrides or takes away fails to read back as what was made.
            if (refusal.StartsWith("What the command made would not read back", StringComparison.Ordinal) && !text.Contains("<style", StringComparison.Ordinal))
            {
                differ.Add(Path.GetFileName(file) + ": " + refusal);
            }
        }

        return new Tally(written, unchanged, refused, differ, clock.Elapsed);
    }

    private static string Says(Tally tally)
        => $"{tally.Written} written, {tally.Unchanged} unchanged in {tally.Took.TotalSeconds:0.0}s; refused: "
           + string.Join("; ", tally.Refused.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} ×{pair.Value}"))
           + "; first differences: " + string.Join(" | ", tally.Differ.Take(8));

    /// <summary>Written wherever it is not refused for a reason the edit gives on purpose, and refused no more often than measured.</summary>
    private static void Holds(Tally tally, int refusals)
    {
        Assert.True(tally.Written > 2800, Says(tally));
        Assert.True(tally.Differ.Count == 0, Says(tally));
        Assert.True(tally.Refused.Keys.All(Meant) && tally.Refused.Values.Sum() <= refusals, Says(tally));
    }

    private static bool Meant(string kind)
        => kind.StartsWith("What the command made would not read back", StringComparison.Ordinal)
           || kind.Contains("is not something a command writes into a drawing.", StringComparison.Ordinal)
           || kind.StartsWith("'transform' is set in this element's style attribute", StringComparison.Ordinal);

    private static IEnumerable<SvgVisualElement> Drawn(SvgDocument document) => document.Children.OfType<SvgVisualElement>().ToList();

    [Fact]
    public void Nothing_Done_Writes_Nothing()
    {
        var tally = Over(_ => null);

        Assert.True(tally.Written == 0 && tally.Refused.Count == 0, Says(tally));
    }

    [Fact]
    public void A_Translate_Put_In_Front_Of_Every_Transform_Reads_Back()
    {
        var tally = Over(context =>
        {
            foreach (var element in Drawn(context.Document))
            {
                var transforms = element.Transforms ?? new SvgTransformCollection();

                transforms.Insert(0, new SvgTranslate(1, 2));
                element.Transforms = transforms;
            }

            return null;
        });

        // One drawing's sheet sets a transform an attribute cannot beat, and one sets it in its style attribute.
        Holds(tally, 2);
    }

    [Fact]
    public void The_Drawing_Reversed_Reads_Back()
    {
        var tally = Over(context =>
        {
            var children = context.Document.Children.ToList();

            foreach (var child in children)
            {
                context.Document.Children.Remove(child);
            }

            foreach (var child in Enumerable.Reverse(children))
            {
                context.Document.Children.Add(child);
            }

            return null;
        });

        Holds(tally, 0);
    }

    [Fact]
    public void Every_Drawn_Element_Copied_Reads_Back()
    {
        var tally = Over(context =>
        {
            foreach (var element in Drawn(context.Document))
            {
                var copy = (SvgElement)element.DeepCopy();

                foreach (var each in copy.Descendants().Prepend(copy))
                {
                    each.ID = null;
                }

                context.Document.Children.Insert(context.Document.Children.IndexOf(element) + 1, copy);
            }

            return null;
        });

        // Six hold an element of another namespace, which is not a command's to write.
        Holds(tally, 6);
    }

    [Fact]
    public void Every_Other_Element_Taken_Out_Reads_Back()
    {
        var tally = Over(context =>
        {
            var children = context.Document.Children.ToList();

            for (var i = 0; i < children.Count; i += 2)
            {
                context.Document.Children.Remove(children[i]);
            }

            return null;
        });

        // Five take out a <style>, and what it set no longer reads the same.
        Holds(tally, 5);
    }
}
