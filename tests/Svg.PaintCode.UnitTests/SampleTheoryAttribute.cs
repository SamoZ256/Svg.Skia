// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Xunit;

namespace Svg.PaintCode.UnitTests;

/// <summary>The <see cref="SampleFactAttribute"/> gate, for a theory.</summary>
public sealed class SampleTheoryAttribute : TheoryAttribute
{
    public SampleTheoryAttribute()
    {
        if (SampleFactAttribute.Path is null)
        {
            Skip = "Set SVG_PAINTCODE_SAMPLE to the path of a PaintCode document to run this.";
        }
    }
}
