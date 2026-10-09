// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace Svg.CodeGen.Skia;

/// <summary>
/// One class calling every drawing the way PaintCode's generated code is called: positional
/// arguments with no defaults, a framed overload, a <c>Context</c> ahead of a text drawing's
/// arguments, and its boxes as <c>out</c> parameters.
/// </summary>
internal static class SkiaCSharpFacade
{
    private const string ResizeHelper = "SvgResize";

    private static readonly string[] s_taken = { "canvas", "context", "targetFrame", "resizing", "resizedFrame" };

    internal static string Build(
        string api,
        IReadOnlyList<(SkiaCSharpDrawing Drawing, IReadOnlyList<SvgExpressionParameter> Kept, bool WithDefaults)> drawings,
        SvgCodeStyle style)
    {
        var segments = api.Split('.');

        if (segments.Any(segment => !IsIdentifier(segment)))
        {
            throw new ExprException($"'{api}' is not a class name the API can be generated as.", 0);
        }

        var className = segments[segments.Length - 1];
        var namespaceName = string.Join(".", segments.Take(segments.Length - 1));

        foreach (var drawing in drawings.Select(d => d.Drawing))
        {
            // A namespace of the class's own name at the top level is what a caller's
            // `VectorIconsResource.drawX` finds first, ahead of any using directive.
            if (drawing.NamespaceName.Split('.')[0] == className
                || drawing.NamespaceName == api
                || drawing.NamespaceName.StartsWith(api + ".", StringComparison.Ordinal))
            {
                throw new ExprException(
                    $"'{drawing.NamespaceName}' would hide the API class '{api}' from its callers. Move the drawings to another namespace.",
                    0);
            }
        }

        // A parameter one drawing only lists is typed by another that declares it.
        var types = new Dictionary<string, ExprType>(StringComparer.Ordinal);

        foreach (var parameter in drawings.SelectMany(d => d.Drawing.Declarations.Parameters))
        {
            if (!types.ContainsKey(parameter.Name))
            {
                types.Add(parameter.Name, parameter.Type);
            }
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<string>();

        foreach (var (drawing, kept, withDefaults) in drawings)
        {
            members.AddRange(Methods(drawing, kept, withDefaults, types, style, signatures));
        }

        var resizing = style.Resizing is { } given ? Qualified(given) : null;

        if (resizing is { })
        {
            members.Add(ResizeMethod(resizing));
        }

        var indent = namespaceName.Length > 0 ? "    " : string.Empty;
        var sb = new StringBuilder();

        if (namespaceName.Length > 0)
        {
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine($"{{");
        }

        // Partial, so the host can keep beside it what PaintCode's class held and no drawing does.
        sb.AppendLine($"{indent}public static partial class {className}");
        sb.AppendLine($"{indent}{{");

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine($"");
            }

            foreach (var line in members[i].Split('\n'))
            {
                sb.AppendLine(line.Length == 0 ? "" : $"{indent}{line}");
            }
        }

        sb.AppendLine($"{indent}}}");

        if (namespaceName.Length > 0)
        {
            sb.AppendLine($"}}");
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Methods(
        SkiaCSharpDrawing drawing,
        IReadOnlyList<SvgExpressionParameter> kept,
        bool withDefaults,
        IReadOnlyDictionary<string, ExprType> types,
        SvgCodeStyle style,
        HashSet<string> signatures)
    {
        var method = drawing.Method ?? "draw" + drawing.ClassName;

        if (!IsIdentifier(method) || method == ResizeHelper)
        {
            throw new ExprException($"'{method}' cannot be the method {drawing.ClassName} is drawn by.", 0);
        }

        var names = drawing.Parameters ?? Ordered(drawing.Declarations.Parameters.Select(p => p.Name).ToList(), style.Order);
        var parameters = new List<(string Name, string Type)>();

        foreach (var name in names)
        {
            var declared = drawing.Declarations.Parameters.FirstOrDefault(p => p.Name == name);

            if (declared is null && !types.ContainsKey(name))
            {
                throw new ExprException($"'{name}' is listed for {method}, and no drawing declares a parameter of that name to take its type from.", 0);
            }

            if (s_taken.Contains(name) || parameters.Any(p => p.Name == name))
            {
                throw new ExprException($"{method} cannot take '{name}': the name is already taken in its signature.", 0);
            }

            parameters.Add((name, ExprCompiler.CSharpTypeOf(declared?.Type ?? types[name])));
        }

        // Positional is what the caller writes, so a parameter Draw requires and the API leaves out
        // has nothing to be given.
        var missing = kept.FirstOrDefault(p => !names.Contains(p.Name) && !(withDefaults && p.DefaultCode() is { }));

        if (missing is { })
        {
            throw new ExprException($"{method} leaves out '{missing.Name}', which {drawing.ClassName}.Draw requires.", 0);
        }

        var target = $"global::{drawing.NamespaceName}.{drawing.ClassName}";
        var call = string.Join(
            ", ",
            new[] { "canvas" }.Concat(kept.Where(p => names.Contains(p.Name)).Select(p => $"{p.Name}: {p.Name}")));

        var outs = drawing.Bounds
            .Select(box => (Parameter: "@" + char.ToLowerInvariant(box.Name[0]) + box.Name.Substring(1), box.Name))
            .ToList();

        var head = new List<string> { "SKCanvas canvas" };

        if (style.Context is { } context && DrawsText(drawing.Picture))
        {
            head.Add($"{Qualified(context)} context");
        }

        var tail = parameters.Select(p => $"{p.Type} {p.Name}")
            .Concat(outs.Select(o => $"out SKRect {o.Parameter}"))
            .ToList();

        var finish = outs.Select(o => $"        {o.Parameter} = {target}.@{o.Name};").ToList();

        yield return Method(method, head.Concat(tail), new[] { $"        {target}.Draw({call});" }.Concat(finish), signatures);

        if (style.Resizing is not { } resizing)
        {
            yield break;
        }

        var frame = drawing.Picture.CullRect;
        var body = new List<string> { $"        var resizedFrame = {ResizeHelper}(resizing, {frame.ToSKRect()}, targetFrame);" };

        body.Add($"        canvas.Save();");
        body.Add($"        canvas.Translate(resizedFrame.Left, resizedFrame.Top);");

        if (frame.Width > 0 && frame.Height > 0)
        {
            body.Add($"        canvas.Scale(resizedFrame.Width / {frame.Width.ToFloatString()}, resizedFrame.Height / {frame.Height.ToFloatString()});");
        }

        if (frame.Left != 0 || frame.Top != 0)
        {
            body.Add($"        canvas.Translate({(-frame.Left).ToFloatString()}, {(-frame.Top).ToFloatString()});");
        }

        body.Add($"        {target}.Draw({call});");
        body.Add($"        canvas.Restore();");
        body.AddRange(finish);

        var framed = head.Concat(new[] { "SKRect targetFrame", $"{Qualified(resizing)} resizing" }).Concat(tail);

        yield return Method(method, framed, body, signatures);
    }

    private static string Method(string name, IEnumerable<string> parameters, IEnumerable<string> body, HashSet<string> signatures)
    {
        var list = parameters.ToList();

        // Overloads differ by their types alone, so two drawings agreeing on both would be CS0111.
        var signature = $"{name}({string.Join(",", list.Select(p => p.Substring(0, p.LastIndexOf(' '))))})";

        if (!signatures.Add(signature))
        {
            throw new ExprException($"Two drawings would both be {signature} on the API class. Give one of them another method name.", 0);
        }

        var sb = new StringBuilder();

        sb.Append($"    public static void {name}({string.Join(", ", list)})\n");
        sb.Append($"    {{\n");

        foreach (var line in body)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append($"    }}");

        return sb.ToString();
    }

    /// <summary>PaintCode's own <c>ResizingBehaviorApply</c>, so a framed drawing lands where it did.</summary>
    private static string ResizeMethod(string resizing)
        => $$"""
                private static SKRect {{ResizeHelper}}({{resizing}} behavior, SKRect rect, SKRect target)
                {
                    if (rect.Equals(target))
                    {
                        return rect;
                    }

                    if (behavior == {{resizing}}.Stretch)
                    {
                        return target;
                    }

                    var xRatio = Math.Abs(target.Width / rect.Width);
                    var yRatio = Math.Abs(target.Height / rect.Height);
                    var scale = behavior == {{resizing}}.AspectFit ? Math.Min(xRatio, yRatio)
                        : behavior == {{resizing}}.AspectFill ? Math.Max(xRatio, yRatio)
                        : behavior == {{resizing}}.Center ? 1f
                        : 0f;
                    var width = Math.Abs(rect.Width * scale);
                    var height = Math.Abs(rect.Height * scale);

                    return new SKRect(target.MidX - width / 2, target.MidY - height / 2, target.MidX + width / 2, target.MidY + height / 2);
                }
            """.Replace("\r\n", "\n");

    private static IReadOnlyList<string> Ordered(IReadOnlyList<string> declared, IReadOnlyList<string> order)
        => declared
            .Select((name, index) => (name, index, rank: order.IndexOf(name)))
            .OrderBy(p => p.rank < 0 ? int.MaxValue : p.rank)
            .ThenBy(p => p.index)
            .Select(p => p.name)
            .ToList();

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool DrawsText(SKPicture picture)
        => picture.Commands?.Any(command => command switch
        {
            DrawTextCanvasCommand or DrawTextBlobCanvasCommand or DrawPositionedTextRunCanvasCommand or DrawTextOnPathCanvasCommand => true,
            DrawPictureCanvasCommand { Picture: { } nested } => DrawsText(nested),
            _ => false
        }) == true;

    private static string Qualified(string type) => type.IndexOf('.') >= 0 && !type.StartsWith("global::", StringComparison.Ordinal) ? "global::" + type : type;

    private static bool IsIdentifier(string name)
        => name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => c == '_' || char.IsLetterOrDigit(c));
}
