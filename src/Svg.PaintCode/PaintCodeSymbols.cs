// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;

namespace Svg.PaintCode;

/// <summary>The canvases a symbol can point at, by both of the names it points with.</summary>
/// <remarks>
/// A <c>PPSymbol</c> carries the target's name and an identifier for it, and the two are not the same
/// string: the identifier is the name with everything but letters and digits taken out. Either can be
/// the one that still matches after a canvas is renamed, so both are indexed.
/// </remarks>
internal sealed class PaintCodeSymbols
{
    private readonly Dictionary<string, PaintCodeCanvas> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaintCodeCanvas> _byIdentifier = new(StringComparer.Ordinal);

    private PaintCodeSymbols()
    {
    }

    internal static PaintCodeSymbols Of(PaintCodeDocument document)
    {
        var symbols = new PaintCodeSymbols();

        foreach (var canvas in document.Canvases)
        {
            symbols._byName[canvas.Name] = canvas;
            symbols._byIdentifier[canvas.Identifier] = canvas;
        }

        return symbols;
    }

    internal PaintCodeCanvas? Find(PaintCodeSymbolItem symbol)
    {
        if (symbol.TargetName.Length > 0 && _byName.TryGetValue(symbol.TargetName, out var byName))
        {
            return byName;
        }

        return symbol.TargetIdentifier.Length > 0 && _byIdentifier.TryGetValue(symbol.TargetIdentifier, out var byIdentifier)
            ? byIdentifier
            : null;
    }
}
