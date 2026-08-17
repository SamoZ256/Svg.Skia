// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using SkiaSharp;

namespace Svg.Skia.UnitTests.Common;

/// <summary>
/// The metadata references a test needs to compile emitted C#: everything this test process is
/// already running against, plus SkiaSharp.
/// </summary>
/// <remarks>
/// Shared because two suites compile generated code for different reasons — one renders a picture
/// and compares pixels, the other evaluates a single expression and compares the value — and the
/// reference list is the part that has to track the target framework rather than the test.
/// </remarks>
internal static class CSharpReferences
{
    private static IReadOnlyList<MetadataReference>? s_references;

    public static IReadOnlyList<MetadataReference> All
    {
        get
        {
            if (s_references is { })
            {
                return s_references;
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);

            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            {
                foreach (var path in trusted.Split(Path.PathSeparator))
                {
                    if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            var skia = typeof(SKColor).Assembly.Location;
            if (!string.IsNullOrEmpty(skia))
            {
                paths.Add(skia);
            }

            s_references = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

            return s_references;
        }
    }
}
