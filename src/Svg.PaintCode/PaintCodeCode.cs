// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace Svg.PaintCode;

/// <summary>
/// The <c>&lt;e:code&gt;</c> block of one drawing: the parameters and locals its expressions reach,
/// and nothing else.
/// </summary>
/// <remarks>
/// Narrowed to what the drawing uses, which is not tidiness: a parameter without a default is
/// required, so an unused one would make every caller of the generated method supply a value that
/// drives nothing.
/// </remarks>
internal sealed class PaintCodeCode
{
    internal static readonly XNamespace Namespace = "https://svg.skia/expr/1.0";

    private readonly PaintCodeDeclarations _declarations;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    internal PaintCodeCode(PaintCodeDeclarations declarations)
    {
        _declarations = declarations;
    }

    internal bool Any => _used.Count > 0;

    /// <summary>Records everything <paramref name="expression"/> names, and what those in turn name.</summary>
    internal void Use(string expression)
    {
        foreach (var name in PaintCodeDeclarations.Names(expression))
        {
            Reach(name);
        }
    }

    internal XElement? Element()
    {
        if (_used.Count == 0)
        {
            return null;
        }

        var code = new XElement(Namespace + "code");
        var lets = new List<PaintCodeDeclaration>();

        foreach (var declaration in Declarations())
        {
            if (declaration.Kind is PaintCodeDeclarationKind.Parameter)
            {
                code.Add(Parameter(declaration));

                continue;
            }

            lets.Add(declaration);
        }

        var written = new HashSet<string>(StringComparer.Ordinal);

        // Depth first over what each local reads, because a local may only name one declared before
        // it -- and the order they were written in is not necessarily that order.
        foreach (var declaration in lets)
        {
            Write(code, declaration, written);
        }

        return code;
    }

    private IEnumerable<PaintCodeDeclaration> Declarations()
    {
        foreach (var name in _used)
        {
            if (_declarations.ByName.TryGetValue(name, out var declaration) &&
                declaration.Kind is PaintCodeDeclarationKind.Parameter or PaintCodeDeclarationKind.Local)
            {
                yield return declaration;
            }
        }
    }

    private void Write(XElement code, PaintCodeDeclaration declaration, HashSet<string> written)
    {
        if (!written.Add(declaration.Name))
        {
            return;
        }

        foreach (var name in PaintCodeDeclarations.Names(declaration.Body ?? string.Empty))
        {
            if (_declarations.ByName.TryGetValue(name, out var needed) && needed.Kind is PaintCodeDeclarationKind.Local)
            {
                Write(code, needed, written);
            }
        }

        code.Add(new XElement(Namespace + "let", new XAttribute("name", declaration.Name), declaration.Body));
    }

    private static XElement Parameter(PaintCodeDeclaration declaration)
    {
        var element = new XElement(
            Namespace + "param",
            new XAttribute("name", declaration.Name),
            new XAttribute("type", declaration.Type ?? "number"));

        if (declaration.Body is { } value)
        {
            element.SetAttributeValue("default", value);
        }

        if (declaration is { Minimum: { } minimum, Maximum: { } maximum })
        {
            element.SetAttributeValue("min", minimum.ToString("0.####", CultureInfo.InvariantCulture));
            element.SetAttributeValue("max", maximum.ToString("0.####", CultureInfo.InvariantCulture));
        }

        // Alone rather than with the pair, the format allowing a step without ends where it refuses
        // one end without the other.
        if (declaration.Step is { } step)
        {
            element.SetAttributeValue("step", step.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return element;
    }

    private void Reach(string name)
    {
        if (!_declarations.ByName.TryGetValue(name, out var declaration) || !_used.Add(name))
        {
            return;
        }

        if (declaration.Kind is PaintCodeDeclarationKind.Local)
        {
            Use(declaration.Body ?? string.Empty);
        }
    }
}
