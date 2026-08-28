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

/// <summary>Where the helper methods a drawing needs are placed.</summary>
public enum SvgHelperScope
{
    /// <summary>Private members of each class. Every class stands alone; several classes in one
    /// file repeat them. The only shape a single-class file can use.</summary>
    PerClass,

    /// <summary>A <c>file</c> scoped class outside every namespace. Invisible beyond the file, so
    /// any number of generated files coexist. Requires C# 11 of whoever compiles the output.</summary>
    FileLocal,

    /// <summary>An <c>internal</c> class outside every namespace, for compilers below C# 11. The
    /// name has to differ per file or two generated files collide.</summary>
    Internal
}

/// <summary>The SkiaSharp the generated code is compiled against.</summary>
public enum SkiaSharpTarget
{
    /// <summary>
    /// 3.x. Paths are built by calling <c>SKPath</c> directly; <c>SKPathBuilder</c> does not
    /// exist yet, and the mutating methods are not obsolete.
    /// </summary>
    V3,

    /// <summary>
    /// 4.x, the default. Every mutating method on <c>SKPath</c> is obsolete, so paths are built
    /// through <c>SKPathBuilder</c> and detached.
    /// </summary>
    V4
}

/// <summary>Whether <c>Draw</c> keeps the picture it built, and whether it guards it.</summary>
public enum SvgPictureCache
{
    /// <summary>Record a picture per call and dispose it. Draw stays stateless.</summary>
    None,

    /// <summary>
    /// Keep the last picture and reuse it while the arguments are unchanged. Draw becomes
    /// stateful and is <b>not</b> safe to call from several threads: one can replace and dispose
    /// the picture another is midway through drawing.
    /// </summary>
    LastValue,

    /// <summary>
    /// As <see cref="LastValue"/>, guarded by a lock held across the draw. Releasing it any
    /// earlier would reopen the race the lock exists to close.
    /// </summary>
    LastValueLocked
}

/// <summary>One drawing to be emitted, for <see cref="SkiaCSharpCodeGen.GenerateFile"/>.</summary>
public sealed class SkiaCSharpDrawing
{
    public SkiaCSharpDrawing(SKPicture picture, string namespaceName, string className, SvgExpressionDeclarations? declarations)
    {
        Picture = picture;
        NamespaceName = namespaceName;
        ClassName = className;
        Declarations = declarations ?? SvgExpressionDeclarations.Empty;
    }

    public SKPicture Picture { get; }

    public string NamespaceName { get; }

    public string ClassName { get; }

    public SvgExpressionDeclarations Declarations { get; }
}

public static class SkiaCSharpCodeGen
{
    public const string DefaultHelperClassName = "SvgExpressionHelpers";

    private const string CacheLockField = "s_cacheLock";

    private const string CachedPictureField = "s_cachedPicture";

    // Every remembered argument shares this prefix, so none of them can collide with the two
    // fields above however the parameter is named.
    private static string ArgumentField(string parameterName) => "s_arg_" + parameterName;

    public static string Generate(SKPicture picture, string namespaceName, string className)
        => Generate(picture, namespaceName, className, SvgExpressionDeclarations.Empty);

    public static string Generate(
        SKPicture picture,
        string namespaceName,
        string className,
        SvgExpressionDeclarations? declarations,
        SvgPictureCache cache = SvgPictureCache.None,
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4)
    {
        var sb = new StringBuilder();

        AppendPreamble(sb, helperClassName: null);

        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine($"{{");
        sb.Append(BuildClass(picture, className, declarations, includeHelpers: true, cache, skiaSharp));
        sb.AppendLine($"}}");

        return sb.ToString();
    }

    /// <summary>
    /// Emits several drawings into one file, sharing their helper methods rather than repeating
    /// them once per class.
    /// </summary>
    /// <remarks>
    /// Drawings keep their declared order, and namespaces the order they first appear in, so the
    /// output of a given batch does not move around between runs.
    /// </remarks>
    public static string GenerateFile(
        IReadOnlyList<SkiaCSharpDrawing> drawings,
        SvgHelperScope scope = SvgHelperScope.FileLocal,
        string helperClassName = DefaultHelperClassName,
        SvgPictureCache cache = SvgPictureCache.None,
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4)
    {
        var shared = scope != SvgHelperScope.PerClass;

        // Namespaces in first-appearance order, and the classes within each in declared order.
        var order = new List<string>();
        var groups = new Dictionary<string, List<SkiaCSharpDrawing>>(StringComparer.Ordinal);

        foreach (var drawing in drawings)
        {
            if (!groups.TryGetValue(drawing.NamespaceName, out var group))
            {
                group = new List<SkiaCSharpDrawing>();
                groups.Add(drawing.NamespaceName, group);
                order.Add(drawing.NamespaceName);
            }

            // Two classes of one name in one namespace is CS0101 in the emitted file, which
            // reports a problem with generated code rather than with the batch that asked for it.
            if (group.Any(other => string.Equals(other.ClassName, drawing.ClassName, StringComparison.Ordinal)))
            {
                throw new ExprException(
                    $"'{drawing.NamespaceName}.{drawing.ClassName}' is generated twice into one file. Give the drawings different class names.",
                    0);
            }

            group.Add(drawing);
        }

        var bodies = drawings.ToDictionary(
            drawing => drawing,
            drawing => BuildClass(drawing.Picture, drawing.ClassName, drawing.Declarations, includeHelpers: !shared, cache, skiaSharp));

        // Which helpers are needed is decided across every class at once, so each appears once.
        var helpers = shared
            ? RequiredHelpers(string.Concat(bodies.Values)).ToList()
            : new List<string[]>();

        var sb = new StringBuilder();

        AppendPreamble(sb, helpers.Count > 0 ? helperClassName : null);

        if (helpers.Count > 0)
        {
            sb.AppendLine($"{(scope == SvgHelperScope.FileLocal ? "file" : "internal")} static class {helperClassName}");
            sb.AppendLine($"{{");

            for (var i = 0; i < helpers.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine($"");
                }

                foreach (var line in helpers[i])
                {
                    // The bodies are written as class members; hoisted they have to be reachable
                    // from the classes that call them.
                    sb.AppendLine(line.Length == 0 ? "" : $"    {line.Replace("private static", "internal static")}");
                }
            }

            sb.AppendLine($"}}");
            sb.AppendLine($"");
        }

        for (var i = 0; i < order.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine($"");
            }

            var group = groups[order[i]];

            sb.AppendLine($"namespace {order[i]}");
            sb.AppendLine($"{{");

            for (var j = 0; j < group.Count; j++)
            {
                if (j > 0)
                {
                    sb.AppendLine($"");
                }

                sb.Append(bodies[group[j]]);
            }

            sb.AppendLine($"}}");
        }

        return sb.ToString();
    }

    // Outside the namespace: a using written inside one resolves relative to it first, so
    // `using SkiaSharp;` inside `namespace Foo` binds to `Foo.SkiaSharp` if the consumer has one.
    private static void AppendPreamble(StringBuilder sb, string? helperClassName)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"");
        sb.AppendLine($"using System;");
        sb.AppendLine($"using SkiaSharp;");

        // Imported statically so call sites stay unqualified, exactly as they are when the
        // helpers are private members of the class that uses them.
        if (helperClassName is { })
        {
            sb.AppendLine($"using static {helperClassName};");
        }

        sb.AppendLine($"");
    }

    private static string BuildClass(
        SKPicture picture,
        string className,
        SvgExpressionDeclarations? declarations,
        bool includeHelpers,
        SvgPictureCache cache,
        SkiaSharpTarget skiaSharp)
    {
        declarations ??= SvgExpressionDeclarations.Empty;

        var (compiler, lets) = declarations.Resolve();

        var counter = new SkiaCSharpCodeGenCounter { SkiaSharpTarget = skiaSharp };
        var indent = "            ";

        // Record the body first: which helper methods the class needs is decided by what the
        // expression compiler actually produced.
        var bodyBuilder = new StringBuilder();
        var counterPicture = ++counter.Picture;

        using (SymCSharpEmitter.UseCompiler(compiler))
        {
            picture.ToSKPicture(counter, bodyBuilder, indent);
        }

        bodyBuilder.AppendLine($"{indent}return {counter.PictureVarName}{counterPicture};");
        var body = bodyBuilder.ToString();

        var parameters = declarations.Parameters;
        var isParameterized = parameters.Count > 0;
        var withDefaults = declarations.EmitsDefaultArguments();
        var parameterList = BuildParameterList(parameters, withDefaults);
        var argumentList = string.Join(", ", parameters.Select(p => p.Name));

        // A document with no parameters already caches better than this: one picture built in
        // the static constructor, drawn with no comparison at all.
        var cached = cache != SvgPictureCache.None && isParameterized;
        var locked = cached && cache == SvgPictureCache.LastValueLocked;

        var sb = new StringBuilder();

        if (isParameterized && !withDefaults)
        {
            // Said where somebody reading the signature will wonder why a parameter the document
            // gives a default is required of them.
            sb.AppendLine($"    // Every argument is required, including the ones this drawing declares a default for:");
            sb.AppendLine($"    // C# takes optional arguments last, and reordering them would change what a positional");
            sb.AppendLine($"    // call means. The declared defaults are in the drawing.");
        }

        sb.AppendLine($"    public static class {className}");
        sb.AppendLine($"    {{");

        if (cached)
        {
            if (locked)
            {
                sb.AppendLine($"        private static readonly object {CacheLockField} = new object();");
                sb.AppendLine($"");
            }

            sb.AppendLine($"        private static SKPicture {CachedPictureField};");
            sb.AppendLine($"");

            foreach (var parameter in parameters)
            {
                sb.AppendLine($"        private static {ExprCompiler.CSharpTypeOf(parameter.Type)} {ArgumentField(parameter.Name)};");
            }

            sb.AppendLine($"");
        }

        // A picture built from parameters cannot be cached in a static field, so the
        // parameterless shape is kept exactly as it was for documents without declarations.
        if (!isParameterized)
        {
            sb.AppendLine($"        public static SKPicture Picture {{ get; }}");
            sb.AppendLine($"");
            sb.AppendLine($"        static {className}()");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            Picture = Record();");
            sb.AppendLine($"        }}");
            sb.AppendLine($"");
        }

        sb.AppendLine($"        {(isParameterized ? "public" : "private")} static SKPicture Record({parameterList})");
        sb.AppendLine($"        {{");

        // Before the lets, since a let may reference the parameter and will have been compiled to
        // read this local.
        var colourFallbacks = declarations.ColourFallbacks();

        foreach (var (parameter, local, defaultCode) in colourFallbacks)
        {
            sb.AppendLine($"{indent}SKColor {local} = {parameter} ?? {defaultCode};");
        }

        foreach (var (name, type, code) in lets)
        {
            sb.AppendLine($"{indent}{ExprCompiler.CSharpTypeOf(type)} {name} = {code};");
        }

        if (lets.Count > 0 || colourFallbacks.Count > 0)
        {
            sb.AppendLine($"");
        }

        sb.Append(body);

        sb.AppendLine($"        }}");
        sb.AppendLine($"");

        if (cached)
        {
            // Drawing happens every frame and arguments move on a hover or a theme flip. A miss
            // costs one comparison per parameter against the cost of recording.
            sb.AppendLine($"        public static void Draw(SKCanvas {counter.CanvasVarName}, {parameterList})");
            sb.AppendLine($"        {{");

            if (locked)
            {
                // The draw stays inside the lock: releasing it earlier would let another thread
                // replace and dispose the picture midway through playback.
                sb.AppendLine($"            lock ({CacheLockField})");
                sb.AppendLine($"            {{");
            }

            // One more level of nesting once the lock is there.
            var cacheIndent = locked ? "                " : "            ";

            // A float argument of NaN never equals itself, so it misses every time. That costs a
            // re-record and nothing else.
            var tests = new List<string> { $"{CachedPictureField} is null" };
            tests.AddRange(parameters.Select(p => $"|| {ArgumentField(p.Name)} != {p.Name}"));

            for (var i = 0; i < tests.Count; i++)
            {
                var open = i == 0 ? "if (" : "    ";
                var close = i == tests.Count - 1 ? ")" : string.Empty;

                sb.AppendLine($"{cacheIndent}{open}{tests[i]}{close}");
            }

            sb.AppendLine($"{cacheIndent}{{");
            sb.AppendLine($"{cacheIndent}    {CachedPictureField}?.Dispose();");
            sb.AppendLine($"{cacheIndent}    {CachedPictureField} = Record({argumentList});");

            foreach (var parameter in parameters)
            {
                sb.AppendLine($"{cacheIndent}    {ArgumentField(parameter.Name)} = {parameter.Name};");
            }

            sb.AppendLine($"{cacheIndent}}}");
            sb.AppendLine($"");
            sb.AppendLine($"{cacheIndent}{counter.CanvasVarName}.DrawPicture({CachedPictureField});");

            if (locked)
            {
                sb.AppendLine($"            }}");
            }

            sb.AppendLine($"        }}");
        }
        else if (isParameterized)
        {
            // Built for this call and unable to escape, so Draw disposes it at a known point.
            // Record keeps its own contract: what it returns, the caller owns.
            sb.AppendLine($"        public static void Draw(SKCanvas {counter.CanvasVarName}, {parameterList})");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            using (var {counter.PictureVarName} = Record({argumentList}))");
            sb.AppendLine($"            {{");
            sb.AppendLine($"                {counter.CanvasVarName}.DrawPicture({counter.PictureVarName});");
            sb.AppendLine($"            }}");
            sb.AppendLine($"        }}");
        }
        else
        {
            sb.AppendLine($"        public static void Draw(SKCanvas {counter.CanvasVarName})");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            {counter.CanvasVarName}.DrawPicture(Picture);");
            sb.AppendLine($"        }}");
        }

        // Scan everything emitted so far, not just the picture body: helpers are just as likely
        // to be referenced from a let or a parameter default.
        if (includeHelpers)
        {
            foreach (var helper in RequiredHelpers(sb.ToString()))
            {
                sb.AppendLine($"");
                foreach (var line in helper)
                {
                    sb.AppendLine(line.Length == 0 ? "" : $"        {line}");
                }
            }
        }

        sb.AppendLine($"    }}");

        return sb.ToString();
    }

    // Optional only where the author gave a default: inventing one would put a value in the
    // signature that appears nowhere in the document.
    /// <summary>The parameters as a C# parameter list, in the order the document declares them.</summary>
    /// <remarks>
    /// The order is the document's, always. Where that leaves C# unable to take the defaults —
    /// its optional arguments have to come last — every argument is emitted as required instead,
    /// which <see cref="SvgCodeDeclarationsExtensions.EmitsDefaultArguments"/> decides once for
    /// this and for the colour locals together.
    /// </remarks>
    private static string BuildParameterList(IReadOnlyList<SvgExpressionParameter> parameters, bool withDefaults)
    {
        var rendered = new List<string>(parameters.Count);

        foreach (var parameter in parameters)
        {
            var type = ExprCompiler.CSharpTypeOf(parameter.Type);
            var code = withDefaults ? parameter.DefaultCode() : null;

            if (code is null)
            {
                rendered.Add($"{type} {parameter.Name}");
                continue;
            }

            // A colour default cannot be a C# argument default, so the parameter goes nullable and
            // coalesces into a local. One without a default stays a required SKColor.
            rendered.Add(parameter.Type == ExprType.Color
                ? $"{type}? {parameter.Name} = null"
                : $"{type} {parameter.Name} = {code}");
        }

        return string.Join(", ", rendered);
    }

    private static IEnumerable<string[]> RequiredHelpers(string body)
    {
        foreach (var helper in ExprHelpers.All)
        {
            if (body.IndexOf(helper.Key + "(", StringComparison.Ordinal) >= 0)
            {
                yield return helper.Value;
            }
        }
    }
}
