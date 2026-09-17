// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace Svg.PaintCode;

/// <summary>A PaintCode document could not be read, or could not be converted.</summary>
public sealed class PaintCodeException : Exception
{
    public PaintCodeException(string message) : base(message)
    {
    }

    public PaintCodeException(string message, Exception inner) : base(message, inner)
    {
    }
}
