using System;
using System.Collections.Generic;
using Svg.Expressions;
using Svg.Model.Services;
using Svg.SceneGraph;
using Svg.Skia;
using ShimPicture = ShimSkiaSharp.SKPicture;

namespace SvgExpressionsDemo;

/// <summary>One load of a document: what it declares, and how to draw it with values.</summary>
public sealed class LivePreviewResult
{
    private readonly ShimPicture? _model;
    private readonly SvgExpressionDeclarations _declarations;

    private LivePreviewResult(
        IReadOnlyList<string> errors,
        IReadOnlyList<SvgExpressionParameter> parameters,
        ShimPicture? model,
        SvgExpressionDeclarations declarations)
    {
        Errors = errors;
        Parameters = parameters;
        _model = model;
        _declarations = declarations;
    }

    public IReadOnlyList<string> Errors { get; }

    /// <summary>What the document's <c>&lt;e:code&gt;</c> block declares, in declaration order.</summary>
    public IReadOnlyList<SvgExpressionParameter> Parameters { get; }

    public bool Success => _model is not null;

    public static LivePreviewResult Failed(IReadOnlyList<string> errors)
        => new(errors, Array.Empty<SvgExpressionParameter>(), null, SvgExpressionDeclarations.Empty);

    internal static LivePreviewResult Succeeded(ShimPicture model, SvgExpressionDeclarations declarations)
        => new(Array.Empty<string>(), declarations.Parameters, model, declarations);

    /// <summary>
    /// Evaluates the document's expressions against <paramref name="values"/> and draws the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned picture belongs to the caller and should be disposed. That is deliberate: this is
    /// called from the render thread, so a picture created and disposed inside one draw never crosses
    /// a thread boundary. An application that renders on the UI thread wants
    /// <see cref="SKSvg.SetExpressionValues"/> instead, which keeps one picture and swaps it in place.
    /// </para>
    /// <para>
    /// Every declared parameter must have a value here — evaluation is strict, exactly as the
    /// generated code is, where a parameter without a default is simply required.
    /// </para>
    /// </remarks>
    /// <exception cref="ExprException">A value is missing, of the wrong type, or an expression does not check.</exception>
    public SkiaSharp.SKPicture? Render(IReadOnlyDictionary<string, ExprValue> values)
        => _model is null ? null : LivePreview.ToPicture(SvgSceneExpressionEvaluator.Evaluate(_model, _declarations, values));
}

/// <summary>
/// Runs the real pipeline at runtime: SVG text to a scene model, then that model evaluated against
/// live values.
/// </summary>
/// <remarks>
/// <para>
/// The two halves have very different costs, and separating them is the point. Parsing a document and
/// compiling its scene is the expensive part and only happens when the text changes. Changing a value
/// re-evaluates the model that is already there — no parse, no scene compile, no code generation.
/// </para>
/// <para>
/// This used to generate C# and compile it with Roslyn into a collectible
/// <c>AssemblyLoadContext</c> whose <c>Record</c> was invoked by reflection, because evaluating an
/// expression was something only the code generator could do. It is not any more.
/// </para>
/// </remarks>
public sealed class LivePreview
{
    private static readonly SkiaModel s_skiaModel = new(new SKSvgSettings());

    private static readonly Svg.Model.ISvgAssetLoader s_assetLoader = new SkiaSvgAssetLoader(s_skiaModel);

    public LivePreviewResult Load(string svgText)
    {
        try
        {
            var document = SvgService.FromSvg(svgText);
            if (document is null)
            {
                return LivePreviewResult.Failed(new[] { "The document could not be parsed as SVG." });
            }

            var model = SvgSceneRuntime.CreateModel(document, s_assetLoader);
            if (model?.Commands is null)
            {
                return LivePreviewResult.Failed(new[] { "The document produced no drawing commands." });
            }

            // Read from the parsed document rather than from the text it came from, which is what a
            // host with only a SvgDocument in hand has to do.
            return LivePreviewResult.Succeeded(model, document.ExpressionDeclarations);
        }
        catch (ExprException error)
        {
            // An expression problem is a diagnostic about the document, not a crash. ToDiagnostic
            // points a caret at the offending character, which is something the old Roslyn errors
            // could never do — those named a line of generated C# the author never saw.
            return LivePreviewResult.Failed(new[] { error.ToDiagnostic() });
        }
        catch (Exception error)
        {
            return LivePreviewResult.Failed(new[] { error.Message });
        }
    }

    internal static SkiaSharp.SKPicture? ToPicture(ShimPicture? model)
        => model is null ? null : s_skiaModel.ToSKPicture(model);
}
