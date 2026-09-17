// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.IO;
using Xunit;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// A fact that runs only when <c>SVG_PAINTCODE_SAMPLE</c> names a PaintCode document, and reports
/// itself skipped otherwise.
/// </summary>
/// <remarks>
/// The sample these tests were written against is 16 MB and belongs to a product rather than to this
/// repository, so it cannot be committed; a test that quietly passed without it would be worse than
/// one that says it did not run.
/// </remarks>
public sealed class SampleFactAttribute : FactAttribute
{
    public SampleFactAttribute()
    {
        if (Path is null)
        {
            Skip = "Set SVG_PAINTCODE_SAMPLE to the path of a PaintCode document to run this.";
        }
    }

    internal static string? Path
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("SVG_PAINTCODE_SAMPLE");

            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
        }
    }
}
