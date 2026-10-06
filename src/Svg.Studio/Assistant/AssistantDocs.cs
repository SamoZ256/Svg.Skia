// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Svg.Studio;

/// <summary>One heading's worth of a document the assistant answers from.</summary>
public sealed record AssistantDocSection(string Id, string Title, string Text);

/// <summary>The site's user-facing articles, embedded, cleaned and cut at their headings.</summary>
/// <remarks>
/// A model with room for all of it is given <see cref="Whole"/>; a small one is given
/// <see cref="Contents"/> and reads a section at a time, which is why the cut is at headings.
/// </remarks>
public static class AssistantDocs
{
    private static readonly Regex ScribanEscape = new(@"\{%\{(.*?)\}%\}", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Lazy<IReadOnlyList<AssistantDocSection>> Loaded = new(Load);

    public static IReadOnlyList<AssistantDocSection> Sections => Loaded.Value;

    public static string Whole => string.Join("\n\n", Sections.Select(section => section.Text));

    public static string Contents => string.Join("\n", Sections.Select(section => $"{section.Id}  {section.Title}"));

    /// <summary>Roughly what <see cref="Whole"/> costs a model, rounded up for the code blocks in it.</summary>
    public static int EstimatedTokens => Whole.Length / 3;

    public static string? Read(string id)
        => Sections.FirstOrDefault(section => string.Equals(section.Id, id, StringComparison.OrdinalIgnoreCase))?.Text;

    /// <summary>The article as a reader sees it: no front matter, and <c>{{ }}</c> as it is typed.</summary>
    /// <remarks>
    /// The site writes <c>{%{{{ x }}}%}</c> so Lunet leaves the braces alone. Handed over like that, a
    /// model learns the escape as the syntax and suggests it back.
    /// </remarks>
    internal static string Clean(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");

        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---\n", 4, StringComparison.Ordinal) is var end and > 0)
        {
            text = text[(end + 5)..];
        }

        return ScribanEscape.Replace(text, match => match.Groups[1].Value).Trim();
    }

    /// <summary>Cuts an article at its <c>##</c> and <c>###</c> headings, leaving fenced code alone.</summary>
    public static IEnumerable<AssistantDocSection> Split(string document, string text)
    {
        var title = document;
        var slug = document;
        var body = new StringBuilder();
        var fenced = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            if (!fenced && line.StartsWith("# ", StringComparison.Ordinal) && body.Length == 0)
            {
                title = line[2..].Trim();
            }
            else if (!fenced && (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal)))
            {
                if (body.ToString().Trim() is { Length: > 0 } done)
                {
                    yield return new AssistantDocSection(slug, title, done);
                }

                title = line.TrimStart('#').Trim();
                slug = document + "#" + Slug(title);
                body.Clear();
            }

            body.Append(line).Append('\n');
        }

        if (body.ToString().Trim() is { Length: > 0 } last)
        {
            yield return new AssistantDocSection(slug, title, last);
        }
    }

    private static string Slug(string title)
        => Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static IReadOnlyList<AssistantDocSection> Load()
    {
        var assembly = typeof(AssistantDocs).Assembly;
        var sections = new List<AssistantDocSection>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith("Docs/", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);

            var document = Path.GetFileNameWithoutExtension(name);

            // A heading repeated inside one article would give two sections one id, and read_doc the first.
            foreach (var section in Split(document, Clean(reader.ReadToEnd())))
            {
                var id = section.Id;

                for (var n = 2; sections.Any(taken => taken.Id == id); n++)
                {
                    id = $"{section.Id}-{n}";
                }

                sections.Add(section with { Id = id });
            }
        }

        return sections;
    }
}
