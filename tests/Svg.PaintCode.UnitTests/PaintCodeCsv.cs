// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>Enough CSV for a field that has to hold a sentence.</summary>
/// <remarks>
/// These files are written and read only here, so this is the comma-and-quote rule and nothing else:
/// a field holding a comma or a quote is wrapped, and a quote inside one is doubled.
/// </remarks>
internal static class PaintCodeCsv
{
    internal static IReadOnlyList<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var at = 0; at < line.Length; at++)
        {
            if (quoted)
            {
                if (line[at] != '"')
                {
                    field.Append(line[at]);
                }
                else if (at + 1 < line.Length && line[at + 1] == '"')
                {
                    field.Append('"');
                    at++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            if (line[at] == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (line[at] == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(line[at]);
            }
        }

        fields.Add(field.ToString());

        return fields;
    }

    internal static string Field(string value)
        => value.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
