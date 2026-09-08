// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Svg.Expressions;

namespace Svg;

/// <summary>
/// Writes the expressions a drawing consumes while it is being built into the document, and takes
/// them back out again.
/// </summary>
/// <remarks>
/// The other kind of expression survives into the recorded drawing and is rebound by rewriting it.
/// These do not: a typeface is resolved during compilation, the text is measured with it, and the
/// positions are baked. The only place their value can be applied is the document the compiler is
/// about to read.
///
/// Restored rather than left behind, because the document is the one the host keeps: it is what
/// <c>GetXML</c> writes, what the JavaScript DOM sees, and what the next compile reads. A scope that
/// did not put it back would leave a saved file holding one binding's values.
///
/// A snapshot and not a <c>DeepCopy</c>, following the shape of
/// <see cref="SvgDocument.BeginUseInstanceStyleScope"/>: a copy costs a reflection walk per element
/// and would need its deferred paint servers rebinding.
/// </remarks>
public static class SvgExpressionSubstitution
{
    /// <summary>A scope that substituted nothing, for a document with none to substitute.</summary>
    public static IDisposable None { get; } = new Scope(null);

    /// <summary>Whether anything in <paramref name="document"/> is resolved before recording.</summary>
    /// <remarks>
    /// Asked before every compile and before every generate, so it is a walk that stops at the first
    /// one rather than a list.
    /// </remarks>
    public static bool IsNeeded(SvgDocument? document) => Carriers(document).Any();

    /// <summary>Every element carrying one, with the names it carries.</summary>
    /// <remarks>
    /// Public because a code generator has to refuse such a document by name: a picture it bakes has
    /// already been measured with these values, so a parameter driving one could never vary.
    /// </remarks>
    public static IEnumerable<(SvgElement Element, string Name)> Carriers(SvgDocument? document)
    {
        if (document is null)
        {
            yield break;
        }

        foreach (var element in Elements(document))
        {
            foreach (var name in NamesOn(element))
            {
                yield return (element, name);
            }
        }
    }

    /// <summary>
    /// Why <paramref name="document"/> cannot be generated as C#, or null when it can.
    /// </summary>
    /// <remarks>
    /// Generated code replays a picture that was recorded at build time, with the text already
    /// measured and the glyph positions already written down as numbers. A value the compile consumed
    /// is therefore frozen into it, and a generated signature offering to vary one would be offering
    /// something it cannot do. Refusing says so once, where it can still be acted on.
    /// </remarks>
    public static string? WhyNotGeneratable(SvgDocument? document)
    {
        foreach (var (element, name) in Carriers(document))
        {
            var what = name == SvgExpressionAttributes.ContentName
                ? $"the text of <{element.ElementName}>"
                : $"'{name}' on <{element.ElementName}>";

            return $"{what} is resolved before the drawing is recorded -- the text is measured with it and the positions are baked -- so a generated picture cannot vary it. Bind it at run time with SKSvg.SetExpressionValues, or write the value as a literal to generate from.";
        }

        return null;
    }

    /// <summary>
    /// Substitutes what <paramref name="evaluator"/> resolves, until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// An expression that will not evaluate is left alone rather than thrown over. Loading is
    /// documented never to evaluate anything, precisely so that a malformed block cannot stop a
    /// document that renders perfectly well from opening, and this runs on the load path.
    /// </remarks>
    public static IDisposable Begin(SvgDocument? document, ExprEvaluator evaluator)
    {
        if (evaluator is null)
        {
            throw new ArgumentNullException(nameof(evaluator));
        }

        var scope = new Scope(document);

        foreach (var (element, name) in Carriers(document))
        {
            if (SvgExpressionAttributes.Lifted(element.CustomAttributes, name) is not { } expression)
            {
                continue;
            }

            ExprValue value;
            try
            {
                value = evaluator.EvaluateTo(expression, TypeOf(name), Describe(name));
            }
            catch (Exception failure) when (failure is ExprException or ArgumentException)
            {
                // Left alone rather than thrown over, and clamp throws ArgumentException rather than
                // the language's own. The source view already marks a bad expression where it is
                // written; what matters here is that the document still opens.
                continue;
            }

            scope.Write(element, name, Written(value));
        }

        return scope;
    }

    /// <summary>What the language's value looks like written into a document.</summary>
    /// <remarks>
    /// A third spelling, and deliberately not either of the other two: <see cref="ExprValue.ToString"/>
    /// quotes a string because it writes a literal of the language, and this writes an attribute
    /// value, where the quotes would be part of the font's name.
    /// </remarks>
    public static string Written(ExprValue value) => value.Type switch
    {
        ExprType.String => value.AsString,
        ExprType.Number => value.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        ExprType.Boolean => value.AsBoolean ? "true" : "false",
        _ => value.ToString()
    };

    private static ExprType TypeOf(string name)
        => name == SvgExpressionAttributes.ContentName
            ? SvgExpressionAttributes.ContentType
            : SvgExpressionAttributes.TypeFor(name) ?? ExprType.String;

    private static string Describe(string name)
        => name == SvgExpressionAttributes.ContentName
            ? "The text of an element"
            : $"An expression in '{name}'";

    private static IEnumerable<SvgElement> Elements(SvgDocument document)
    {
        yield return document;

        foreach (var descendant in document.Descendants())
        {
            yield return descendant;
        }
    }

    private static IEnumerable<string> NamesOn(SvgElement element)
    {
        // The declarations block is never touched. SvgDocument.ExpressionDeclarations is read off
        // the live DOM on every access, so writing into it would change the declarations under the
        // compile that is reading them.
        if (element.CustomAttributes.Count == 0)
        {
            yield break;
        }

        if (SvgExpressionAttributes.Lifted(element.CustomAttributes, SvgExpressionAttributes.ContentName) is { })
        {
            yield return SvgExpressionAttributes.ContentName;
        }

        foreach (var name in SvgExpressionAttributes.Supported)
        {
            if (SvgExpressionAttributes.IsResolvedBeforeRecording(name) &&
                SvgExpressionAttributes.Lifted(element.CustomAttributes, name) is { })
            {
                yield return name;
            }
        }
    }

    /// <summary>What was written, and what was there before it.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly SvgDocument? _document;
        private readonly List<(SvgElement Element, string Name, object? Previous)> _written = new();

        public Scope(SvgDocument? document)
        {
            _document = document;
        }

        public void Write(SvgElement element, string name, string value)
        {
            if (name == SvgExpressionAttributes.ContentName)
            {
                _written.Add((element, name, Content(element)));
                SetContent(element, value);

                return;
            }

            _written.Add((element, name, element.GetAnimationValue(name)));
            element.TrySetAnimationValue(name, _document, CultureInfo.InvariantCulture, value);
        }

        public void Dispose()
        {
            // Backwards, so an element written twice ends on what it started with.
            for (var index = _written.Count - 1; index >= 0; index--)
            {
                var (element, name, previous) = _written[index];

                if (name == SvgExpressionAttributes.ContentName)
                {
                    SetContent(element, previous as string ?? string.Empty);

                    continue;
                }

                if (previous is null)
                {
                    element.ClearAnimationValue(name);

                    continue;
                }

                element.TrySetAnimationValue(name, _document, CultureInfo.InvariantCulture, previous);
            }

            _written.Clear();
        }

        private static string Content(SvgElement element)
            => string.Concat(element.Nodes.OfType<SvgContentNode>().Select(node => node.Content));

        /// <remarks>
        /// The nodes and <see cref="SvgElement.Content"/> both, because different things read them:
        /// the scene compiler walks the nodes and never looks at Content, while GetXML and the
        /// JavaScript DOM read Content.
        /// </remarks>
        private static void SetContent(SvgElement element, string value)
        {
            var written = false;

            foreach (var node in element.Nodes.OfType<SvgContentNode>())
            {
                node.Content = written ? string.Empty : value;
                written = true;
            }

            if (!written)
            {
                element.Nodes.Add(new SvgContentNode { Content = value });
            }

            element.Content = value;
        }
    }
}
