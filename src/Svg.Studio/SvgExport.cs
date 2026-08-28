using System;
using System.IO;
using System.Text;
using Svg.CodeGen.Skia;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// Writes an open drawing somewhere else: as SVG, or as the C# that draws it.
/// </summary>
/// <remarks>
/// The name decides which, because a picker hands back a path and not the filter that was chosen
/// to arrive at it.
/// </remarks>
public static class SvgExport
{
    /// <summary>The namespace generated code goes in, which is the one svgc puts it in unasked.</summary>
    public const string Namespace = "Svg";

    public static bool IsCSharp(string path)
        => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes <paramref name="source"/> to <paramref name="path"/>, in the form it names.</summary>
    /// <exception cref="InvalidOperationException">The drawing could not be built to generate from.</exception>
    public static void Write(SvgViewerDocument document, string source, string path)
    {
        if (!IsCSharp(path))
        {
            // Through the document, so a drawing that came in with a byte order mark keeps it.
            document.Write(source, path);
            return;
        }

        File.WriteAllText(path, Generate(document, source, ClassName(path)));
    }

    /// <summary>The C# that draws <paramref name="source"/>.</summary>
    /// <remarks>
    /// Built again rather than generated from the open picture: the viewer binds the panel's values
    /// into its model, and a model that has them in it already emits a <c>Draw</c> whose parameters
    /// nothing reads. What the drawing declares belongs in the signature, which is what svgc emits.
    /// </remarks>
    public static string Generate(SvgViewerDocument document, string source, string className)
    {
        using var rebuilt = document.Reload(source);

        if (rebuilt.DeclarationError is { } error)
        {
            throw new InvalidOperationException(error);
        }

        if (rebuilt.Svg.Model is not { } picture)
        {
            throw new InvalidOperationException("The drawing compiled to nothing to generate from.");
        }

        return SkiaCSharpCodeGen.Generate(picture, Namespace, className, rebuilt.Declarations);
    }

    /// <summary>The generated class's name: the file's own, made into an identifier.</summary>
    public static string ClassName(string path)
    {
        var name = new StringBuilder();

        foreach (var c in Path.GetFileNameWithoutExtension(path) ?? string.Empty)
        {
            name.Append(c == '_' || char.IsLetterOrDigit(c) ? c : '_');
        }

        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            name.Insert(0, '_');
        }

        return name.ToString();
    }
}
