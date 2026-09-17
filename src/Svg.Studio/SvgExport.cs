using System;
using System.IO;
using System.Text;
using Svg.CodeGen.Skia;
using Svg.Skia;
using Svg.SourceEditing;
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
    /// <param name="size">
    /// The size a project builds this drawing at, or none for the size it was written with.
    /// </param>
    /// <returns>The file it went to, which is <see cref="PathFor"/> of the path given.</returns>
    /// <exception cref="InvalidOperationException">The drawing could not be built to generate from.</exception>
    public static string Write(SvgViewerDocument document, string source, string path, SvgSizeRequest size)
    {
        var target = PathFor(path);
        var sized = Sized(document, source, size);

        if (IsCSharp(target))
        {
            File.WriteAllText(target, Generate(document, sized, ClassName(target)));
        }
        else
        {
            // Through the document, so a drawing that came in with a byte order mark keeps it. The
            // C# form gets that through Reload.
            document.Write(Flat(document.Built(sized)), target);
        }

        return target;
    }

    /// <summary>
    /// The drawing's text at <paramref name="size"/>, or as written when nothing asks for one.
    /// </summary>
    /// <remarks>
    /// Resized once, here, so both forms carry it: the C# is generated from this rather than
    /// resized again, and the two cannot come out disagreeing about how big the drawing is. What
    /// the screen shows is what a project builds, so an export that ignored the project handed back
    /// something the viewer never showed.
    ///
    /// A size the drawing cannot take is not a reason to refuse the export — it is the same answer
    /// the viewer gives, which is to draw it at the size it has.
    /// </remarks>
    private static string Sized(SvgViewerDocument document, string source, SvgSizeRequest size)
    {
        if (size.IsEmpty)
        {
            return source;
        }

        if (SvgSourceDocument.Read(source, out _) is not { } tree)
        {
            return source;
        }

        // The size it was asked for where the drawing can take it, and the size it has where it
        // cannot: an export is not the place to report that.
        return document.Resize(tree, size) is null ? tree.ToText() : source;
    }

    /// <summary>
    /// The drawing without the indentation it was written at inside something else.
    /// </summary>
    /// <remarks>
    /// A drawing a project holds inline carries the project's indentation, which is the project's
    /// business and not the drawing's: written out as a file of its own it should read as one. The
    /// shallowest line decides, and only lines that have that much are shifted, so a line inside a
    /// stylesheet or a run of text keeps what it says.
    /// </remarks>
    private static string Flat(string svgText)
    {
        var lines = svgText.Split('\n');
        var depth = int.MaxValue;

        for (var line = 1; line < lines.Length; line++)
        {
            var text = lines[line];

            if (text.Trim().Length == 0)
            {
                continue;
            }

            depth = Math.Min(depth, text.Length - text.TrimStart(' ').Length);
        }

        if (depth is 0 or int.MaxValue)
        {
            return svgText;
        }

        var indent = new string(' ', depth);

        for (var line = 1; line < lines.Length; line++)
        {
            if (lines[line].StartsWith(indent, StringComparison.Ordinal))
            {
                lines[line] = lines[line].Substring(depth);
            }
        }

        return string.Join("\n", lines);
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

        if ((rebuilt.Svg.WhyExpressionsNotBindable ??
             SvgExpressionSubstitution.WhyNotGeneratable(rebuilt.Svg.SourceDocument)) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        if (rebuilt.Svg.Model is not { } picture)
        {
            throw new InvalidOperationException("The drawing compiled to nothing to generate from.");
        }

        return SkiaCSharpCodeGen.Generate(picture, Namespace, className, rebuilt.Declarations);
    }

    /// <summary>The generated class's name: the file's own, made into an identifier.</summary>
    public static string ClassName(string path) => Identifier(Path.GetFileNameWithoutExtension(path) ?? string.Empty);

    /// <summary>A name as C# will take it: anything else becomes an underscore.</summary>
    /// <remarks>
    /// Also what a project's drawing falls back on, which is why this is not simply the tail of
    /// <see cref="ClassName"/>: a drawing is named rather than filed, and a name is not a path.
    /// </remarks>
    public static string Identifier(string text)
    {
        var name = new StringBuilder();

        foreach (var c in text)
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
