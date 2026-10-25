// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.SourceEditing;

namespace Svg.Studio.UnitTests;

/// <summary>
/// A project written the way one reads it — a <c>&lt;studio&gt;</c> holding groups and drawings —
/// made into the folder Studio keeps it as, and read back out the same way.
/// </summary>
/// <remarks>
/// So a test still says its project in one piece. Made through the document itself rather than by
/// writing files, so what lands on disk is what Studio would have written.
/// </remarks>
internal static class TestPackage
{
    private static XNamespace Declarations => SvgExpressionDeclarations.Namespace;

    /// <summary>The files of the project <paramref name="xml"/> describes, by path inside its folder.</summary>
    public static IReadOnlyDictionary<string, string> From(string xml)
    {
        var source = SvgSourceDocument.Read(xml, out var refusal)
                     ?? throw new SvgcProjectException(refusal ?? "That is not well formed XML.");
        var studio = source.Document.Root!;

        if (studio.Name != "studio")
        {
            throw new SvgcProjectException($"The project root must be <studio>, but was <{studio.Name.LocalName}>.");
        }

        var document = ProjectDocument.Empty();

        Settle(source, studio, document.Root);

        return document.Files();
    }

    /// <summary>The project <paramref name="xml"/> describes, as Studio reads it from its folder.</summary>
    public static ProjectDocument Parse(string xml, string? path = null) => ProjectDocument.Read(From(xml), path);

    /// <summary>Writes the project <paramref name="xml"/> describes as a folder at <paramref name="path"/>.</summary>
    public static string Write(string path, string xml)
    {
        foreach (var (relative, text) in From(xml))
        {
            var file = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, new UTF8Encoding(false));
        }

        return path;
    }

    /// <summary>The project as one piece of text, groups and drawings nested, for a test to read.</summary>
    public static string Dump(ProjectDocument document)
    {
        var text = new StringBuilder();

        Open(text, "studio", document.Root, string.Empty);
        Blocks(text, document.Root, "  ");

        foreach (var child in document.Root.Children)
        {
            Node(text, child, "  ");
        }

        text.Append("</studio>\n");

        return text.ToString();
    }

    /// <summary><see cref="Dump(ProjectDocument)"/>, of the project saved at <paramref name="path"/>.</summary>
    public static string Dump(string path) => Dump(ProjectDocument.Load(path));

    /// <summary>What is saved at <paramref name="path"/>: a project dumped, or a drawing's text.</summary>
    public static string Read(string path) => Directory.Exists(path) ? Dump(path) : File.ReadAllText(path);

    /// <summary>The project <paramref name="xml"/> describes as <see cref="Read"/> gives it back once saved.</summary>
    public static string Normal(string xml) => Dump(Parse(xml));

    private static void Settle(SvgSourceDocument source, XElement from, ProjectGroup into)
    {
        foreach (var attribute in from.Attributes().Where(attribute => attribute.Name != "name"))
        {
            into.Element.SetAttributeValue(attribute.Name, attribute.Value);
        }

        foreach (var child in from.Elements())
        {
            if (child.Name == Declarations + "code")
            {
                Refused(into.SetCode(child.ToString(SaveOptions.DisableFormatting)));
            }
            else if (child.Name == Declarations + "templates" && into is ProjectRoot root)
            {
                Refused(root.SetTemplates(child.ToString(SaveOptions.DisableFormatting)));
            }
            else if (child.Name == "group")
            {
                Settle(source, child, into.AddGroup(Name(child)));
            }
            else if (child.Name == "drawing")
            {
                var svgs = child.Elements().ToList();

                if (svgs.Count != 1 || svgs[0].Name.LocalName != "svg")
                {
                    throw new SvgcProjectException($"<drawing name=\"{Name(child)}\"> must hold one <svg> and nothing else.");
                }

                var drawing = into.AddDrawing(Name(child), source.TextOf(svgs[0]));

                foreach (var attribute in child.Attributes().Where(attribute => attribute.Name != "name"))
                {
                    drawing.Element.SetAttributeValue(attribute.Name, attribute.Value);
                }
            }
            else
            {
                throw new SvgcProjectException($"<{child.Name.LocalName}> is not allowed in <{from.Name.LocalName}>.");
            }
        }

        static string Name(XElement element)
            => ((string?)element.Attribute("name"))?.Trim() is { Length: > 0 } name
                ? name
                : throw new SvgcProjectException($"<{element.Name.LocalName}> is missing a name.");

        static void Refused(string? refusal)
        {
            if (refusal is { })
            {
                throw new SvgcProjectException(refusal);
            }
        }
    }

    private static void Node(StringBuilder text, ProjectNode node, string indent)
    {
        if (node is ProjectDrawing drawing)
        {
            Open(text, "drawing", drawing, indent);
            text.Append(indent).Append("  ").Append(drawing.Text).Append('\n');
            text.Append(indent).Append("</drawing>\n");

            return;
        }

        var group = (ProjectGroup)node;

        Open(text, "group", group, indent);
        Blocks(text, group, indent + "  ");

        foreach (var child in group.Children)
        {
            Node(text, child, indent + "  ");
        }

        text.Append(indent).Append("</group>\n");
    }

    private static void Open(StringBuilder text, string name, ProjectNode node, string indent)
    {
        text.Append(indent).Append('<').Append(name);

        // The name first, wherever the file had it: it is what the old format led with.
        foreach (var attribute in node.Element.Attributes()
                     .Where(attribute => !attribute.IsNamespaceDeclaration)
                     .OrderBy(attribute => attribute.Name == "name" ? 0 : 1))
        {
            text.Append(' ').Append(attribute.Name.LocalName).Append("=\"").Append(attribute.Value).Append('"');
        }

        text.Append(">\n");
    }

    private static void Blocks(StringBuilder text, ProjectGroup group, string indent)
    {
        if (group.Code is { })
        {
            text.Append(indent).Append(group.CodeText).Append('\n');
        }

        if (group is ProjectRoot { TemplatesBlock: { } } root)
        {
            text.Append(indent).Append(root.TemplatesText).Append('\n');
        }
    }
}
