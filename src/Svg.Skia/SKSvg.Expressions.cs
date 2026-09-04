// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using ShimSkiaSharp;
using Svg.Expressions;
using Svg.SceneGraph;

namespace Svg.Skia;

public partial class SKSvg
{
    /// <summary>
    /// The picture as compiled, with its expressions still in it. Kept so that changing a value
    /// re-evaluates rather than re-parsing the document and recompiling the scene.
    /// </summary>
    private SKPicture? _symbolicModel;

    /// <summary>
    /// Values bound to the document's parameters, or null when none have been supplied — which is
    /// the state after a plain load, and the state in which the placeholders render.
    /// </summary>
    private Dictionary<string, ExprValue>? _expressionValues;

    /// <summary>
    /// Everything this document's <c>&lt;e:code&gt;</c> block declares, in declaration order. Empty
    /// when it has none.
    /// </summary>
    /// <remarks>
    /// Read from the document on each call, since the tree is mutable. Loading deliberately does not
    /// touch the declarations, so a malformed block is reported from here rather than making
    /// <c>Load</c> fail on a document that renders perfectly well as placeholders.
    /// </remarks>
    /// <exception cref="ExprException">The declarations are malformed.</exception>
    public SvgExpressionDeclarations ExpressionDeclarations
        => SourceDocument?.ExpressionDeclarations ?? SvgExpressionDeclarations.Empty;

    /// <inheritdoc cref="ExpressionDeclarations"/>
    public IReadOnlyList<SvgExpressionParameter> ExpressionParameters => ExpressionDeclarations.Parameters;

    /// <summary>
    /// The values currently bound, or null when the drawing is rendering its placeholders.
    /// </summary>
    public IReadOnlyDictionary<string, ExprValue>? ExpressionValues => _expressionValues;

    /// <summary>
    /// Binds <paramref name="values"/> to the document's parameters and re-renders.
    /// </summary>
    /// <remarks>
    /// Every parameter needs a value — supplied here, or the declared <c>default</c> — and one with
    /// neither is an error, the rule generated code enforces by making it required. Nothing changes
    /// unless the whole set resolves, so a failed call leaves the last rendering up; null goes back
    /// to the placeholders. It does not re-parse or recompile, which is what makes a slider drag
    /// affordable.
    /// </remarks>
    /// <returns>The re-rendered picture, or null when no document is loaded.</returns>
    /// <exception cref="ExprException">
    /// A parameter has neither a value nor a default, a supplied value has the wrong type, or an
    /// expression in the document does not type check.
    /// </exception>
    public SkiaSharp.SKPicture? SetExpressionValues(IReadOnlyDictionary<string, ExprValue>? values)
    {
        // Copied, so a caller that keeps mutating its own dictionary cannot change what is being
        // rendered behind our back.
        var bound = CopyValues(values);

        // A value the compile consumes cannot be rebound by rewriting the recorded drawing: the
        // typeface was resolved with it and the text measured against that. Recompiling from the
        // retained document is the answer, and it re-applies the recorded expressions on its way
        // out, so a document using both kinds still gets both.
        if (SvgExpressionSubstitution.IsNeeded(SourceDocument))
        {
            return Recompiled(bound);
        }

        SKPicture? symbolic;
        lock (Sync)
        {
            symbolic = _symbolicModel;
        }

        // Evaluated before anything is assigned, so a throw leaves the current rendering alone.
        var evaluated = symbolic is null ? null : Evaluate(symbolic, bound);

        lock (Sync)
        {
            _expressionValues = bound;

            if (evaluated is { } && ReferenceEquals(_symbolicModel, symbolic))
            {
                Model = evaluated;
            }
        }

        return RebuildFromModel();
    }

    /// <summary>Binds by compiling again, for a document whose values a compile consumes.</summary>
    /// <remarks>
    /// The values are put back on a failure so that the documented rule still holds: nothing changes
    /// unless the whole set resolves, and a failed call leaves the last rendering up. The cheap path
    /// gets that by evaluating before it assigns, which a recompile cannot do.
    /// </remarks>
    private SkiaSharp.SKPicture? Recompiled(Dictionary<string, ExprValue>? bound)
    {
        Dictionary<string, ExprValue>? previous;

        lock (Sync)
        {
            previous = _expressionValues;
            _expressionValues = bound;
        }

        try
        {
            return RefreshFromSourceDocument();
        }
        catch
        {
            lock (Sync)
            {
                _expressionValues = previous;
            }

            throw;
        }
    }

    /// <summary>Goes back to rendering the document's placeholders.</summary>
    public SkiaSharp.SKPicture? ClearExpressionValues() => SetExpressionValues(null);

    /// <summary>
    /// Applies the bound values to a freshly compiled model, or returns it untouched when there are
    /// none.
    /// </summary>
    /// <remarks>
    /// The hook the compile funnel calls. With no values bound this is a null check and nothing else,
    /// so loading a document costs exactly what it did before: the declarations are not even read,
    /// which is what keeps a malformed block from turning a successful load into an exception.
    /// </remarks>
    private SKPicture ApplyExpressionValues(SKPicture model, SvgDocument? document)
    {
        var values = _expressionValues;

        return values is null ? model : Evaluate(model, values, document) ?? model;
    }

    /// <summary>
    /// Substitutes the values a compile consumes into the document, for as long as the returned
    /// scope lives.
    /// </summary>
    /// <remarks>
    /// Wrapped around every compile rather than called by whoever binds a value: a compile can start
    /// from a load, a DOM edit, a script mutation or a retained-scene refresh, and one of those
    /// forgetting would render the document with its text missing.
    ///
    /// With nothing bound the evaluator resolves the declared defaults, which is what makes a plain
    /// load draw what the author described. A declaration block that will not resolve at all gives
    /// back the empty scope: loading is documented never to fail on one.
    /// </remarks>
    private IDisposable BeginExpressionSubstitution(SvgDocument? document)
    {
        if (document is null || !SvgExpressionSubstitution.IsNeeded(document))
        {
            return SvgExpressionSubstitution.None;
        }

        try
        {
            return SvgExpressionSubstitution.Begin(
                document,
                ExprEvaluator.Create(document.ExpressionDeclarations, _expressionValues));
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return SvgExpressionSubstitution.None;
        }
    }

    private SKPicture? Evaluate(SKPicture model, IReadOnlyDictionary<string, ExprValue>? values)
        => values is null ? model : Evaluate(model, values, SourceDocument);

    // Written out rather than using the Dictionary(IEnumerable, comparer) constructor, which does not
    // exist on netstandard2.0 or net461.
    internal static Dictionary<string, ExprValue>? CopyValues(IReadOnlyDictionary<string, ExprValue>? values)
    {
        if (values is null)
        {
            return null;
        }

        var copy = new Dictionary<string, ExprValue>(values.Count, StringComparer.Ordinal);

        foreach (var pair in values)
        {
            copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    private static SKPicture? Evaluate(
        SKPicture model,
        IReadOnlyDictionary<string, ExprValue> values,
        SvgDocument? document)
    {
        var declarations = document?.ExpressionDeclarations ?? SvgExpressionDeclarations.Empty;

        return SvgSceneExpressionEvaluator.Evaluate(model, declarations, values);
    }
}
