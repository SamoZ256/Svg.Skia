// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.CodeGen.Skia.Projects;

/// <summary>
/// A fault in a project file, or in a value that could have come from one. Reported like a
/// compiler diagnostic rather than as a crash, the same way recipe and expression errors are.
/// </summary>
public sealed class SvgcProjectException : Exception
{
    public SvgcProjectException(string message) : base(message)
    {
    }

    public SvgcProjectException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
