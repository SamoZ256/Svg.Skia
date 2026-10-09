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

/// <summary>How a drawing's <c>Draw</c> puts it on the canvas it is given.</summary>
public enum SvgBody
{
    /// <summary>Record a picture of the drawing, then draw that picture.</summary>
    Record,

    /// <summary>
    /// Draw onto the canvas itself, from paths and paints built once where nothing the caller
    /// passes changes them. Nothing is left to cache, so it does not combine with <see cref="SvgPictureCache"/>.
    /// </summary>
    Direct
}

/// <summary>Shapes of the generated file beyond the drawings themselves.</summary>
/// <remarks>Every member left at its default emits what the generator always has.</remarks>
public sealed class SvgCodeStyle
{
    public static SvgCodeStyle Default { get; } = new();

    public SvgBody Body { get; init; }

    /// <summary>
    /// A C# expression of type <c>SKTypeface</c> in place of the font-manager lookup, with
    /// <c>{family}</c>, <c>{family-dashed}</c>, <c>{weight}</c> (Regular, Medium, Bold…),
    /// <c>{weightValue}</c> and <c>{slant}</c> written in from what the document asked for.
    /// </summary>
    public string? Typeface { get; init; }

    /// <summary>
    /// The full name of one class forwarding to every drawing in PaintCode's calling convention,
    /// or null for none.
    /// </summary>
    public string? Api { get; init; }

    /// <summary>The host's <c>ResizingBehavior</c> enum, which adds a framed overload per drawing.</summary>
    public string? Resizing { get; init; }

    /// <summary>The type of the argument PaintCode takes ahead of the others where a drawing has text.</summary>
    public string? Context { get; init; }

    /// <summary>The order <see cref="Api"/> takes parameters in; names not listed follow, as declared.</summary>
    public IReadOnlyList<string> Order { get; init; } = Array.Empty<string>();
}

/// <summary>One drawing to be emitted, for <see cref="SkiaCSharpCodeGen.GenerateFile"/>.</summary>
public sealed class SkiaCSharpDrawing
{
    public SkiaCSharpDrawing(
        SKPicture picture,
        string namespaceName,
        string className,
        SvgExpressionDeclarations? declarations,
        IReadOnlyList<string>? frozen = null,
        IReadOnlyList<(string Name, SKRect Rect)>? bounds = null)
    {
        Picture = picture;
        NamespaceName = namespaceName;
        ClassName = className;
        Declarations = declarations ?? SvgExpressionDeclarations.Empty;
        Frozen = frozen ?? Array.Empty<string>();
        Bounds = bounds ?? Array.Empty<(string, SKRect)>();
    }

    public SKPicture Picture { get; }

    public string NamespaceName { get; }

    public string ClassName { get; }

    public SvgExpressionDeclarations Declarations { get; }

    /// <summary>
    /// The expressions the picture could not carry, whose values are written into it instead.
    /// </summary>
    /// <remarks>
    /// A parameter these are the only use of has nothing left to do, and is left off the signature
    /// rather than offered as something the drawing would ignore. One the author declared and never
    /// used anywhere stays, because that is a signature they wrote on purpose.
    /// </remarks>
    public IReadOnlyList<string> Frozen { get; }

    /// <summary>
    /// Boxes the drawing reserves for its host to lay something of its own into, each emitted as a
    /// constant of the class, in the picture's coordinates.
    /// </summary>
    public IReadOnlyList<(string Name, SKRect Rect)> Bounds { get; }

    /// <summary>Its method on <see cref="SvgCodeStyle.Api"/>; <c>draw</c> and the class name when null.</summary>
    public string? Method { get; init; }

    /// <summary>
    /// Every parameter its <see cref="SvgCodeStyle.Api"/> method takes, in order, where
    /// <see cref="SvgCodeStyle.Order"/> does not give PaintCode's. One the drawing does not declare
    /// takes its type from another drawing that does, and is accepted and ignored.
    /// </summary>
    public IReadOnlyList<string>? Parameters { get; init; }
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
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4,
        IReadOnlyList<string>? frozen = null)
    {
        var sb = new StringBuilder();

        AppendPreamble(sb, helperClassName: null);

        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine($"{{");
        sb.Append(BuildClass(picture, className, declarations, includeHelpers: true, cache, skiaSharp, frozen, bounds: null, SvgCodeStyle.Default).Text);
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
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4,
        SvgCodeStyle? style = null)
    {
        style ??= SvgCodeStyle.Default;
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

        var built = drawings.ToDictionary(
            drawing => drawing,
            drawing => BuildClass(drawing.Picture, drawing.ClassName, drawing.Declarations, includeHelpers: !shared, cache, skiaSharp, drawing.Frozen, drawing.Bounds, style));

        var facade = style.Api is { } api
            ? SkiaCSharpFacade.Build(api, drawings.Select(drawing => (drawing, built[drawing].Parameters, built[drawing].WithDefaults)).ToList(), style)
            : null;

        // Which helpers are needed is decided across every class at once, so each appears once.
        var helpers = shared
            ? RequiredHelpers(string.Concat(built.Values.Select(c => c.Text))).ToList()
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

                sb.Append(built[group[j]].Text);
            }

            sb.AppendLine($"}}");
        }

        if (facade is { })
        {
            sb.AppendLine($"");
            sb.Append(facade);
        }

        return sb.ToString();
    }

    // Outside the namespace: a using written inside one resolves relative to it first, so
    // `using SkiaSharp;` inside `namespace Foo` binds to `Foo.SkiaSharp` if the consumer has one.
    /// <summary>The parameters still worth a signature, once something has been frozen.</summary>
    /// <remarks>
    /// A value a compile consumed and the picture could not carry -- text frozen into the positions
    /// it was measured at -- leaves a parameter nothing reads, and a signature offering to vary
    /// something it cannot is the one thing this generator has always refused to emit. Read off what
    /// was emitted rather than predicted, the same way the helper methods are chosen; a name that
    /// only appears inside a string literal keeps its parameter, which is the safe way to be wrong.
    /// </remarks>
    private static IReadOnlyList<SvgExpressionParameter> Read(
        IReadOnlyList<SvgExpressionParameter> parameters,
        IReadOnlyList<(string Parameter, string Local, string DefaultCode, ExprType Type)> computedDefaults,
        string emitted,
        IReadOnlyList<string>? frozen)
    {
        if (frozen is not { Count: > 0 })
        {
            return parameters;
        }

        var kept = new List<SvgExpressionParameter>();

        foreach (var parameter in parameters)
        {
            // Only a parameter something frozen was the use of: one the author declared and never
            // used at all is a signature they wrote, and not this to take away.
            if (!frozen.Any(expression => Mentions(expression, parameter.Name)))
            {
                kept.Add(parameter);
                continue;
            }

            // Through its local where it has one: that is the name the body was compiled to read.
            var name = computedDefaults.FirstOrDefault(fallback => string.Equals(fallback.Parameter, parameter.Name, StringComparison.Ordinal)).Local
                       ?? parameter.Name;

            if (Mentions(emitted, name))
            {
                kept.Add(parameter);
            }
        }

        return kept.Count == parameters.Count ? parameters : kept;
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="name"/> as an identifier.</summary>
    private static bool Mentions(string text, string name)
    {
        for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !IsIdentifier(text[at - 1]);
            var after = at + name.Length == text.Length || !IsIdentifier(text[at + name.Length]);

            if (before && after)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifier(char c) => c == '_' || char.IsLetterOrDigit(c);

    /// <summary>Refuses a box whose name the emitted class could not declare.</summary>
    /// <remarks>
    /// The cache's private fields on top of the shared rule, which an editor asks too and which has no
    /// business knowing them.
    /// </remarks>
    private static void RefuseBounds(string className, IReadOnlyList<(string Name, SKRect Rect)> bounds)
    {
        for (var i = 0; i < bounds.Count; i++)
        {
            var name = bounds[i].Name;
            var why = SvgExpressionAttributes.WhyNotBox(name, className, bounds.Take(i).Select(box => box.Name));

            if (why is null && (name is CacheLockField or CachedPictureField || name.StartsWith(ArgumentField(string.Empty), StringComparison.Ordinal)))
            {
                why = $"'{name}' is a name the generated class already uses.";
            }

            if (why is { })
            {
                throw new ExprException($"'{className}' cannot declare that box: {why}", 0);
            }
        }
    }

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

    /// <summary>A drawing's class, and the signature its <c>Draw</c> ended up with.</summary>
    private sealed record BuiltClass(string Text, IReadOnlyList<SvgExpressionParameter> Parameters, bool WithDefaults);

    private static BuiltClass BuildClass(
        SKPicture picture,
        string className,
        SvgExpressionDeclarations? declarations,
        bool includeHelpers,
        SvgPictureCache cache,
        SkiaSharpTarget skiaSharp,
        IReadOnlyList<string>? frozen,
        IReadOnlyList<(string Name, SKRect Rect)>? bounds,
        SvgCodeStyle style)
    {
        declarations ??= SvgExpressionDeclarations.Empty;
        bounds ??= Array.Empty<(string, SKRect)>();

        var direct = style.Body == SvgBody.Direct;

        if (direct && cache != SvgPictureCache.None)
        {
            throw new ExprException($"'{className}' is drawn directly, which leaves no picture to cache. Set the cache to none.", 0);
        }

        RefuseBounds(className, bounds);

        var (compiler, lets) = declarations.Resolve();

        var counter = new SkiaCSharpCodeGenCounter { SkiaSharpTarget = skiaSharp, TypefaceTemplate = style.Typeface };
        var indent = "            ";

        // Record the body first: which helper methods the class needs is decided by what the
        // expression compiler actually produced.
        var bodyBuilder = new StringBuilder();
        var counterPicture = ++counter.Picture;
        var statics = direct ? new StringBuilder() : null;

        counter.Direct = direct;
        counter.Statics = statics;

        using (SymCSharpEmitter.UseCompiler(compiler))
        {
            picture.ToSKPicture(counter, bodyBuilder, indent);
        }

        counter.Statics = null;

        if (!direct)
        {
            bodyBuilder.AppendLine($"{indent}return {counter.PictureVarName}{counterPicture};");
        }

        var body = bodyBuilder.ToString();

        // Everything the class will say, which is what decides who is read. The lets are in it
        // because a let is emitted whether or not the body uses what it computed.
        var emitted = new StringBuilder(body);

        foreach (var let in lets)
        {
            emitted.Append(' ').Append(let.Code);
        }

        var read = emitted.ToString();

        // Before the lets, since a let may reference the parameter and will have been compiled to
        // read this local.
        var computedDefaults = declarations.ComputedDefaults()
            .Where(fallback => Mentions(read, fallback.Local))
            .ToList();

        var parameters = Read(declarations.Parameters, computedDefaults, read, frozen);
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

        foreach (var (name, rect) in bounds)
        {
            // Verbatim, so a keyword is a name like any other: the member is still called what it says.
            sb.AppendLine($"        public static readonly SKRect @{name} = {rect.ToSKRect()};");
        }

        if (bounds.Count > 0)
        {
            sb.AppendLine($"");
        }

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

        foreach (var (type, name) in counter.StaticFields)
        {
            sb.AppendLine($"        private static readonly {type} s_{name};");
        }

        if (counter.StaticFields.Count > 0)
        {
            sb.AppendLine($"");
        }

        if (!isParameterized)
        {
            sb.AppendLine($"        public static SKPicture Picture {{ get; }}");
            sb.AppendLine($"");
        }

        // A picture built from parameters cannot be cached in a static field, so the
        // parameterless shape is kept exactly as it was for documents without declarations.
        if (!isParameterized || counter.StaticFields.Count > 0)
        {
            sb.AppendLine($"        static {className}()");
            sb.AppendLine($"        {{");
            sb.Append(statics);

            // After the fields, which the drawing it records reads.
            if (!isParameterized)
            {
                sb.AppendLine($"            Picture = Record();");
            }

            sb.AppendLine($"        }}");
            sb.AppendLine($"");
        }

        if (direct)
        {
            sb.AppendLine($"        {(isParameterized ? "public" : "private")} static SKPicture Record({parameterList})");
            sb.AppendLine($"        {{");
            sb.AppendLine($"            using var {counter.PictureRecorderVarName} = new SKPictureRecorder();");
            sb.AppendLine($"            Draw({string.Join(", ", new[] { $"{counter.PictureRecorderVarName}.BeginRecording({picture.CullRect.ToSKRect()})" }.Concat(parameters.Select(p => p.Name)))});");
            sb.AppendLine($"            return {counter.PictureRecorderVarName}.EndRecording();");
            sb.AppendLine($"        }}");
            sb.AppendLine($"");
            sb.AppendLine($"        public static void Draw({string.Join(", ", new[] { $"SKCanvas {counter.CanvasVarName}" }.Concat(isParameterized ? new[] { parameterList } : Array.Empty<string>()))})");
        }
        else
        {
            sb.AppendLine($"        {(isParameterized ? "public" : "private")} static SKPicture Record({parameterList})");
        }

        sb.AppendLine($"        {{");

        foreach (var (parameter, local, defaultCode, type) in computedDefaults)
        {
            // A local whose parameter was dropped is still read by a let, and falls straight to the
            // default there being nothing to coalesce with.
            var value = parameters.Any(kept => string.Equals(kept.Name, parameter, StringComparison.Ordinal))
                ? $"{parameter} ?? {defaultCode}"
                : defaultCode;

            sb.AppendLine($"{indent}{ExprCompiler.CSharpTypeOf(type)} {local} = {value};");
        }

        foreach (var (name, type, code) in lets)
        {
            sb.AppendLine($"{indent}{ExprCompiler.CSharpTypeOf(type)} {name} = {code};");
        }

        if (lets.Count > 0 || computedDefaults.Count > 0)
        {
            sb.AppendLine($"");
        }

        sb.Append(body);

        sb.AppendLine($"        }}");

        // Drawn directly, Draw is the body above; caching was refused for it.
        if (cached)
        {
            sb.AppendLine($"");

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
        else if (isParameterized && !direct)
        {
            sb.AppendLine($"");

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
        else if (!direct)
        {
            sb.AppendLine($"");
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

        return new BuiltClass(sb.ToString(), parameters, withDefaults);
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

            // A default that is not a C# compile-time constant arrives as null and coalesces into
            // a local. A colour takes the `?`, being a value type; a string is already a reference
            // and must not, or generated code would warn (CS8632) in a project with nullable
            // annotations off.
            rendered.Add(parameter.Type switch
            {
                ExprType.Color => $"{type}? {parameter.Name} = null",
                ExprType.String => $"{type} {parameter.Name} = null",

                // Named rather than left to a default arm: each of these is a value type whose
                // default is usually a C# constant and can stand in the signature as written.
                ExprType.Number or ExprType.Integer or ExprType.Boolean => $"{type} {parameter.Name} = {code}",
                _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {parameter.Type}.")
            });
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
