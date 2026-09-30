// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Svg.Studio;

/// <summary>Draws a reply's Markdown as controls: paragraphs, headings, lists, code, and emphasis.</summary>
/// <remarks>
/// Markdig parses; the drawing is here because Markdown.Avalonia stops at Avalonia 11 and this is
/// Avalonia 12. Only what a reply uses is drawn - a table or an image comes out as its text - and
/// what is drawn is selectable, so an attribute value can be copied out of a reply.
/// </remarks>
public static class AssistantMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly FontFamily Mono = new("Menlo, Consolas, monospace");

    /// <summary>The brush behind code, the theme's own so it follows light and dark.</summary>
    public const string CodeBrush = "SystemControlBackgroundBaseLowBrush";

    public static Control Render(string markdown, double fontSize = 12)
    {
        var blocks = new StackPanel { Spacing = 6 };

        foreach (var block in Markdown.Parse(markdown ?? string.Empty, Pipeline))
        {
            blocks.Children.Add(Block(block, fontSize));
        }

        return blocks;
    }

    private static Control Block(Markdig.Syntax.Block block, double fontSize) => block switch
    {
        HeadingBlock heading => Paragraph(heading.Inline, fontSize + Math.Max(0, 4 - heading.Level), FontWeight.SemiBold),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, fontSize, FontWeight.Normal),
        ListBlock list => List(list, fontSize),
        FencedCodeBlock code => Code(code.Lines.ToString(), fontSize),
        CodeBlock code => Code(code.Lines.ToString(), fontSize),
        QuoteBlock quote => new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = Brushes.Gray,
            Padding = new Thickness(8, 0, 0, 0),
            Child = Blocks(quote, fontSize)
        },
        ThematicBreakBlock => new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.4 },
        LeafBlock leaf => Code(leaf.Lines.ToString(), fontSize),
        ContainerBlock container => Blocks(container, fontSize),
        _ => new TextBlock()
    };

    private static Control Blocks(ContainerBlock container, double fontSize)
    {
        var panel = new StackPanel { Spacing = 4 };

        foreach (var child in container)
        {
            panel.Children.Add(Block(child, fontSize));
        }

        return panel;
    }

    private static Control List(ListBlock list, double fontSize)
    {
        var rows = new StackPanel { Spacing = 2 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new DockPanel { LastChildFill = true };
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•",
                FontSize = fontSize,
                Width = 18,
                VerticalAlignment = VerticalAlignment.Top
            };

            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(Blocks(item, fontSize));
            rows.Children.Add(row);
        }

        return rows;
    }

    private static Control Code(string text, double fontSize)
    {
        var box = new SelectableTextBlock
        {
            Text = text.TrimEnd('\n', '\r'),
            FontSize = fontSize - 1,
            FontFamily = Mono,
            TextWrapping = TextWrapping.Wrap
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Child = box
        };

        border[!Border.BackgroundProperty] = new DynamicResourceExtension(CodeBrush);

        return border;
    }

    private static Control Paragraph(ContainerInline? inlines, double fontSize, FontWeight weight)
    {
        var text = new SelectableTextBlock { FontSize = fontSize, FontWeight = weight, TextWrapping = TextWrapping.Wrap };

        if (inlines is { })
        {
            Inlines(inlines, text.Inlines!, fontSize);
        }

        return text;
    }

    private static void Inlines(ContainerInline container, InlineCollection into, double fontSize)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    into.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = Mono, FontSize = fontSize - 1 };
                    run[!TextElement.BackgroundProperty] = new DynamicResourceExtension(CodeBrush);
                    into.Add(run);
                    break;
                case EmphasisInline emphasis:
                    var span = emphasis.DelimiterCount >= 2 ? new Bold() : (Span)new Italic();
                    Inlines(emphasis, span.Inlines, fontSize);
                    into.Add(span);
                    break;
                case LineBreakInline lineBreak:
                    into.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case LinkInline link:
                    // The text, and the address after it where they differ: nothing here opens a browser.
                    var shown = new Span();
                    Inlines(link, shown.Inlines, fontSize);
                    into.Add(shown);

                    if (link.Url is { Length: > 0 } url && !string.Equals(url, Text(link), StringComparison.Ordinal))
                    {
                        into.Add(new Run($" ({url})") { Foreground = Brushes.Gray });
                    }

                    break;
                case ContainerInline nested:
                    Inlines(nested, into, fontSize);
                    break;
                case LeafInline leaf:
                    into.Add(new Run(leaf.ToString()));
                    break;
            }
        }
    }

    private static string Text(ContainerInline container)
        => string.Concat(container.Select(inline => inline is LiteralInline literal ? literal.Content.ToString() : string.Empty));
}
