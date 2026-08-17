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
    /// The parameters this document's <c>&lt;e:code&gt;</c> block declares, in declaration order.
    /// Empty when it has none.
    /// </summary>
    /// <remarks>
    /// Read from the document on each call, since the tree is mutable. Loading deliberately does not
    /// touch the declarations, so a malformed block is reported from here rather than making
    /// <c>Load</c> fail on a document that renders perfectly well as placeholders.
    /// </remarks>
    /// <exception cref="ExprException">The declarations are malformed.</exception>
    public IReadOnlyList<SvgExpressionParameter> ExpressionParameters
        => SourceDocument?.ExpressionDeclarations.Parameters ?? Array.Empty<SvgExpressionParameter>();

    /// <summary>
    /// The values currently bound, or null when the drawing is rendering its placeholders.
    /// </summary>
    public IReadOnlyDictionary<string, ExprValue>? ExpressionValues => _expressionValues;

    /// <summary>
    /// Binds <paramref name="values"/> to the document's parameters and re-renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every parameter has to end up with a value: the one supplied here, or the <c>default</c> the
    /// document declares. A parameter with neither is an error, which is the same rule generated code
    /// enforces by making such a parameter required. A host that would rather see a placeholder can
    /// bind one as a value — <c>#808080</c> for a paint — so leniency needs no support here.
    /// </para>
    /// <para>
    /// Nothing is changed unless the whole set resolves, so a failed call leaves the previous
    /// rendering in place rather than a half-applied one. Passing null goes back to the placeholders.
    /// </para>
    /// <para>
    /// This does not re-parse the document or recompile the scene. It evaluates against the retained
    /// symbolic model and converts the result, which is what makes dragging a slider affordable.
    /// </para>
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
