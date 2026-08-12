// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.Expressions.Recipes;

/// <summary>
/// A fault in the recipe or in the document it is applied to. Reported like a compiler
/// diagnostic rather than as a crash, the same way expression errors are.
/// </summary>
public sealed class SvgRecipeException : Exception
{
    public SvgRecipeException(string message) : base(message)
    {
    }

    public SvgRecipeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
