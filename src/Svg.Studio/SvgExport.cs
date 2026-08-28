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
/// The name says which. That is not a guess about what the author meant: the save panel appends
/// the extension belonging to the type chosen in it, so by the time a path reaches here the choice
/// is already in the name — and a file is then what it is called, whatever route it arrived by.
/// </remarks>
public static class SvgExport
{
    /// <summary>The namespace generated code goes in, which is the one svgc puts it in unasked.</summary>
    public const string Namespace = "Svg";

    public static bool IsCSharp(string path)
        => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file a path names, given an extension when it has none.</summary>
    /// <remarks>
    /// A backstop rather than a rule: every picker that offers file types names the file after the
    /// chosen one, so this only catches a path that arrived some other way.
    /// </remarks>
    public static string PathFor(string path) => Path.HasExtension(path) ? path : path + ".svg";

    /// <summary>Writes <paramref name="source"/> to <paramref name="path"/>, in the form it names.</summary>
    /// <returns>The file it went to, which is <see cref="PathFor"/> of the path given.</returns>
    /// <exception cref="InvalidOperationException">The drawing could not be built to generate from.</exception>
    public static string Write(SvgViewerDocument document, string source, string path)
    {
        var target = PathFor(path);

        if (IsCSharp(target))
        {
            File.WriteAllText(target, Generate(document, source, ClassName(target)));
        }
        else
        {
            // Through the document, so a drawing that came in with a byte order mark keeps it.
            document.Write(source, target);
        }

        return target;
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
