// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// The <c>&lt;e:code&gt;</c> block, written into the tree rather than into the text it came from.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here decides what is legal. A proposed declaration goes through the language's own
/// builder before anything is touched, and the document is read back afterwards, exactly as the span
/// half does — so the two cannot come to disagree about what a person is told.
/// </para>
/// <para>
/// Reading the document back is not a formality. Reordering is a change of meaning rather than of
/// layout, because a let resolves against what is declared above it; and a rename has to carry every
/// use with it or the drawing goes on parsing and stops drawing. Both are caught by asking the
/// language what the document now says, which is the same question the span half asks.
/// </para>
/// </remarks>
public static partial class SvgDeclarationEditor
{
    /// <inheritdoc cref="Add(string, SvgExpressionParameter)"/>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? Add(SvgSourceDocument source, SvgExpressionParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        return Place(
            source,
            (declarations, _) => Rejected(declarations, parameter),
            prefix => Rendered(prefix, parameter),
            isLet: false,
            parameter.Name);
    }

    /// <inheritdoc cref="AddLet(string, string, string)"/>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? AddLet(SvgSourceDocument source, string name, string expression)
        => Place(
            source,
            (declarations, _) => Rejected(declarations, name, expression),
            prefix => Rendered(prefix, name, expression),
            isLet: true,
            name);

    /// <inheritdoc cref="Update(string, string, SvgExpressionParameter)"/>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? Update(SvgSourceDocument source, string name, SvgExpressionParameter replacement)
    {
        if (replacement is null)
        {
            throw new ArgumentNullException(nameof(replacement));
        }

        return Edit(source, (document, declarations, before) =>
        {
            if (Find(document, "param", name) is not { } element)
            {
                return $"This drawing declares no parameter called '{name}'.";
            }

            if (Rejected(declarations, replacement, replacing: name) is { } bad)
            {
                return bad;
            }

            var current = declarations.Parameters.First(p => string.Equals(p.Name, name, StringComparison.Ordinal));

            if (current.Type != replacement.Type)
            {
                // Everything naming it was checked against the type it had, so changing one is a
                // change to every expression that uses it rather than to the declaration alone.
                return $"'{name}' is a {ExprFunctions.Describe(current.Type)} and cannot become a "
                       + $"{ExprFunctions.Describe(replacement.Type)}. Remove it and declare it again.";
            }

            element.SetAttributeValue("default", replacement.DefaultExpression);
            element.SetAttributeValue("min", replacement.MinExpression);
            element.SetAttributeValue("max", replacement.MaxExpression);
            element.SetAttributeValue("step", replacement.StepExpression);

            var renamed = !string.Equals(name, replacement.Name, StringComparison.Ordinal);

            if (renamed)
            {
                element.SetAttributeValue("name", replacement.Name);

                if (SvgDeclarationReferences.Walk(document, name, replacement.Name, out _) is { } trouble)
                {
                    return trouble;
                }
            }

            return Verify(source, before, renamed ? replacement.Name : null);
        });
    }

    /// <inheritdoc cref="Remove(string, string)"/>
    /// <returns>The sentence refusing it, or null where it was taken away.</returns>
    public static string? Remove(SvgSourceDocument source, string name) => Take(source, name, "param");

    /// <inheritdoc cref="RemoveLet(string, string)"/>
    /// <returns>The sentence refusing it, or null where it was taken away.</returns>
    public static string? RemoveLet(SvgSourceDocument source, string name) => Take(source, name, "let");

    /// <inheritdoc cref="UpdateLet(string, string, string, string)"/>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? UpdateLet(SvgSourceDocument source, string name, string newName, string expression)
        => Edit(source, (document, declarations, before) =>
        {
            if (Find(document, "let", name) is not { } element)
            {
                return $"This drawing declares no let called '{name}'.";
            }

            if (Rejected(declarations, newName, expression, replacing: name) is { } bad)
            {
                return bad;
            }

            element.Value = expression;

            var renamed = !string.Equals(name, newName, StringComparison.Ordinal);

            if (renamed)
            {
                element.SetAttributeValue("name", newName);

                if (SvgDeclarationReferences.Walk(document, name, newName, out _) is { } trouble)
                {
                    return trouble;
                }
            }

            return Verify(source, before, renamed ? newName : null);
        });

    /// <inheritdoc cref="MoveLet(string, string, int)"/>
    /// <returns>The sentence refusing it, or null where it was moved.</returns>
    public static string? MoveLet(SvgSourceDocument source, string name, int toIndex)
        => Shift(source, name, toIndex, "let");

    /// <inheritdoc cref="MoveParameter(string, string, int)"/>
    /// <returns>The sentence refusing it, or null where it was moved.</returns>
    public static string? MoveParameter(SvgSourceDocument source, string name, int toIndex)
        => Shift(source, name, toIndex, "param");

    /// <inheritdoc cref="Set(string, string, SvgDeclarationPart, string?)"/>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? Set(SvgSourceDocument source, string name, SvgDeclarationPart part, string? expression)
        => SetAll(source, new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = expression }, part);

    /// <inheritdoc cref="SetDefaults(string, IReadOnlyDictionary{string, string})"/>
    /// <returns>The sentence refusing it, or null where they were written.</returns>
    public static string? SetDefaults(SvgSourceDocument source, IReadOnlyDictionary<string, string> byName)
    {
        if (byName is null)
        {
            throw new ArgumentNullException(nameof(byName));
        }

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var pair in byName)
        {
            wanted[pair.Key] = pair.Value;
        }

        return SetAll(source, wanted, SvgDeclarationPart.Default);
    }

    private static string? SetAll(
        SvgSourceDocument source,
        IReadOnlyDictionary<string, string?> wanted,
        SvgDeclarationPart part)
    {
        if (part == SvgDeclarationPart.Name)
        {
            return "Renaming moves every use of the name too, which Update does.";
        }

        if (Attribute(part) is not { } attributeName)
        {
            return $"{part} is not an attribute a declaration can be given.";
        }

        return Edit(source, (document, _, before) =>
        {
            foreach (var pair in wanted)
            {
                if (Find(document, "param", pair.Key) is not { } element)
                {
                    return $"This drawing declares no parameter called '{pair.Key}'.";
                }

                element.SetAttributeValue(attributeName, pair.Value);
            }

            return Verify(source, before, null);
        });
    }

    private static string? Take(SvgSourceDocument source, string name, string kind)
        => Edit(source, (document, _, before) =>
        {
            if (Find(document, kind, name) is not { } element)
            {
                return $"This drawing declares no {kind} called '{name}'.";
            }

            if (SvgDeclarationReferences.Walk(document, name, null, out var used) is { } trouble)
            {
                return trouble;
            }

            if (used > 0)
            {
                return used == 1
                    ? $"'{name}' is still used once. Take that use away first, or the drawing stops rendering."
                    : $"'{name}' is still used {used} times. Take those uses away first, or the drawing stops rendering.";
            }

            SvgElementEditor.Cut(element);

            return Verify(source, before, null);
        });

    private static string? Shift(SvgSourceDocument source, string name, int toIndex, string kind)
        => Edit(source, (document, _, before) =>
        {
            var siblings = Declared(document, kind);
            var from = siblings.FindIndex(
                candidate => string.Equals((string?)candidate.Attribute("name"), name, StringComparison.Ordinal));

            if (from < 0)
            {
                return $"This drawing declares no {kind} called '{name}'.";
            }

            if (siblings.Select(sibling => sibling.Parent).Distinct().Count() > 1)
            {
                return "This drawing spreads its declarations over more than one <e:code> block, so their order is not one list to reorder.";
            }

            toIndex = Math.Max(0, Math.Min(toIndex, siblings.Count - 1));

            if (toIndex == from)
            {
                return null;
            }

            var moved = siblings[from];
            var indent = SvgElementEditor.Indent(moved);
            var rest = siblings.Where((_, index) => index != from).ToList();

            SvgElementEditor.Cut(moved);
            SvgElementEditor.Put(
                rest[toIndex == 0 ? 0 : toIndex - 1],
                toIndex == 0 ? SvgElementDrop.Before : SvgElementDrop.After,
                moved,
                indent);

            return Verify(source, before, name);
        });

    /// <summary>Writes a declaration into the block, making the block and the namespace if needed.</summary>
    private static string? Place(
        SvgSourceDocument source,
        Func<SvgExpressionDeclarations, XDocument, string?> reject,
        Func<XNamespace, XElement> render,
        bool isLet,
        string name)
        => Edit(source, (document, declarations, before) =>
        {
            if (reject(declarations, document) is { } bad)
            {
                return bad;
            }

            var root = document.Root!;
            var prefix = SvgExpressionDeclarations.NamespacePrefixFor(root, out var declared);

            if (!declared && prefix.Length > 0)
            {
                root.SetAttributeValue(XNamespace.Xmlns + prefix, Ns.NamespaceName);
            }

            var block = Block(source, root);
            var element = render(Ns);

            // Each joins its own group rather than the end of the block: a parameter written below
            // the lets that use it reads backwards, and a let is only in scope for what follows it.
            var after = isLet
                ? block.Elements(Ns + "let").LastOrDefault() ?? block.Elements(Ns + "param").LastOrDefault()
                : block.Elements(Ns + "param").LastOrDefault();

            if (after is { })
            {
                SvgElementEditor.Put(after, SvgElementDrop.After, element, SvgElementEditor.Indent(after));
            }
            else if (block.Elements().FirstOrDefault() is { } first)
            {
                // A parameter with no parameters to join goes above the lets rather than between two
                // of them, where it would split a group whose order is the one thing that matters.
                SvgElementEditor.Put(first, SvgElementDrop.Before, element, SvgElementEditor.Indent(first));
            }
            else
            {
                SvgElementEditor.Put(
                    block,
                    SvgElementDrop.Inside,
                    element,
                    SvgElementEditor.Indent(block) + source.IndentUnit);
            }

            return Verify(source, before, name);
        });

    /// <summary>The block to declare into, made along with its &lt;defs&gt; where there is none.</summary>
    /// <remarks>
    /// Where <c>SvgRecipeRewriter</c> puts it, so a drawing that has been through a recipe and one
    /// that has been through this keep it in the same place.
    /// </remarks>
    private static XElement Block(SvgSourceDocument source, XElement root)
    {
        if (root.Descendants(Ns + "code").FirstOrDefault() is { } existing)
        {
            return existing;
        }

        // A recipe holds its declarations directly. <defs> belongs to SVG, and writing one into a
        // recipe makes a file the recipe reader refuses — after which every drawing built through
        // that recipe silently stops following it, with nothing about either file looking wrong.
        if (root.Name == Ns + "recipe")
        {
            var own = new XElement(Ns + "code");

            SvgElementEditor.First(root, own, SvgElementEditor.Indent(root) + source.IndentUnit);

            return own;
        }

        var defs = root.Elements().FirstOrDefault(element => element.Name == root.Name.Namespace + "defs");

        if (defs is null)
        {
            defs = new XElement(root.Name.Namespace + "defs");
            SvgElementEditor.First(root, defs, SvgElementEditor.Indent(root) + source.IndentUnit);
        }

        var block = new XElement(Ns + "code");

        SvgElementEditor.First(defs, block, SvgElementEditor.Indent(defs) + source.IndentUnit);

        return block;
    }

    /// <summary>Reads the declarations, runs an edit against the tree, and reads them back.</summary>
    private static string? Edit(
        SvgSourceDocument source,
        Func<XDocument, SvgExpressionDeclarations, List<(string Name, ExprException Failure)>, string?> edit)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var declarations = SvgExpressionDeclarations.Parse(source.ToText(), out var diagnostics);

        // What the document already says has to make sense, because the edit is written into the
        // middle of it. An edit elsewhere in the drawing is another matter and does not come here.
        if (diagnostics.Count > 0)
        {
            return $"Fix what the declarations already say first: {diagnostics[0].Message}";
        }

        if (source.Document.Root is null)
        {
            return "The document has no root element to declare anything in.";
        }

        return edit(source.Document, declarations, Unresolved(declarations));
    }

    /// <summary>Reads the document back, so an edit that changed what it means is refused.</summary>
    /// <remarks>
    /// The one check that needs the state before the edit is <paramref name="before"/>: an edit may
    /// not strand a let that resolved, and need not fix one that was already broken.
    /// </remarks>
    private static string? Verify(
        SvgSourceDocument source,
        List<(string Name, ExprException Failure)> before,
        string? expected)
    {
        var declarations = SvgExpressionDeclarations.Parse(source.ToText(), out var diagnostics);

        if (diagnostics.Count > 0)
        {
            return diagnostics[0].Message;
        }

        if (expected is { } name && !Declares(declarations, name))
        {
            return $"'{name}' was written but the document does not read it back.";
        }

        foreach (var (stranded, failure) in Unresolved(declarations))
        {
            if (!before.Any(was => string.Equals(was.Name, stranded, StringComparison.Ordinal)))
            {
                return $"That would leave '{stranded}' unresolved: {failure.Message}";
            }
        }

        return null;
    }

    private static XElement? Find(XDocument document, string kind, string name)
        => Declared(document, kind).FirstOrDefault(
            candidate => string.Equals((string?)candidate.Attribute("name"), name, StringComparison.Ordinal));

    private static XElement Rendered(XNamespace ns, SvgExpressionParameter parameter)
    {
        var element = new XElement(
            ns + "param",
            new XAttribute("name", parameter.Name),
            new XAttribute("type", ExprFunctions.NameOf(parameter.Type)));

        element.SetAttributeValue("default", parameter.DefaultExpression);
        element.SetAttributeValue("min", parameter.MinExpression);
        element.SetAttributeValue("max", parameter.MaxExpression);
        element.SetAttributeValue("step", parameter.StepExpression);

        return element;
    }

    private static XElement Rendered(XNamespace ns, string name, string expression)
        => new(ns + "let", new XAttribute("name", name), expression);
}
