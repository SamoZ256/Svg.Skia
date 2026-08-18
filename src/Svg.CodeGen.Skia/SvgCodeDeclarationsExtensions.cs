// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace Svg.CodeGen.Skia;

/// <summary>
/// The C# half of <see cref="SvgExpressionDeclarations"/>. The declarations themselves describe the
/// document and live beside the language; everything here turns one into generated source, which is
/// why it stayed behind when they moved.
/// </summary>
public static class SvgCodeDeclarationsExtensions
{
    /// <summary>
    /// Type checks the declarations and returns the symbol table every paint expression in the
    /// document is compiled against, along with the C# for each let in declaration order.
    /// </summary>
    public static (ExprCompiler Compiler, IReadOnlyList<(string Name, ExprType Type, string Code)> Lets) Resolve(
        this SvgExpressionDeclarations declarations)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        var symbols = declarations.CreateSymbolTable();
        var compiled = new List<(string, ExprType, string)>();

        // A colour parameter carrying a default reaches the body through a local, so every
        // reference to it has to be emitted as that local's name instead — including references
        // from the lets compiled just below.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fallback in declarations.ColourFallbacks())
        {
            names[fallback.Parameter] = fallback.Local;
        }

        var compiler = new ExprCompiler(symbols, names);

        // Lets resolve in order, so a let may use the ones declared above it but not below. The
        // compiler holds `symbols` by reference, so adding to it here is what makes each let
        // visible to the expressions compiled after it.
        foreach (var let in declarations.Lets)
        {
            var (type, code) = compiler.Compile(let.Expression);
            compiled.Add((let.Name, type, code));
            symbols[let.Name] = type;
        }

        return (compiler, compiled);
    }

    /// <summary>
    /// One entry per colour parameter that declares a default: the parameter's own name, the local
    /// the body reads instead, and the C# for the default it falls back to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A colour cannot be a C# argument default — <c>new SKColor(…)</c> is not a compile-time
    /// constant (CS1736) — so the parameter is emitted as <c>SKColor?</c> defaulting to null and
    /// coalesced into a local at the top of the method. The local exists because C# forbids
    /// shadowing: the body cannot declare a <c>tint</c> that reads from a parameter also called
    /// <c>tint</c>.
    /// </para>
    /// <para>
    /// Computed here rather than in either caller, because <c>Resolve</c> needs the names to rewrite
    /// references and the generator needs them to declare the locals, and the two disagreeing would
    /// emit a body referring to a local that does not exist.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Parameter, string Local, string DefaultCode)> ColourFallbacks(
        this SvgExpressionDeclarations declarations)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        var fallbacks = new List<(string, string, string)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in declarations.Parameters)
        {
            taken.Add(parameter.Name);
        }

        foreach (var let in declarations.Lets)
        {
            taken.Add(let.Name);
        }

        foreach (var parameter in declarations.Parameters)
        {
            if (parameter.Type != ExprType.Color || parameter.DefaultExpression is null)
            {
                continue;
            }

            // An author could declare something called `tint__default` themselves, which would make
            // the generated file fail to compile on a duplicate local rather than do anything
            // subtle. Cheap enough to rule out.
            var local = parameter.Name + "__default";
            while (!taken.Add(local))
            {
                local += "_";
            }

            fallbacks.Add((parameter.Name, local, parameter.DefaultCode()!));
        }

        return fallbacks;
    }

    /// <summary>
    /// Compiles a parameter default, or returns null when the author declared none — in which
    /// case the generated parameter is required rather than carrying an invented value.
    /// </summary>
    public static string? DefaultCode(this SvgExpressionParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        if (parameter.DefaultExpression is null)
        {
            return null;
        }

        // Defaults may not reference other parameters: argument defaults are compile time
        // constants in C#, and an ordering dependency between them would be invisible here. A
        // colour default is refused earlier still, while the declarations are read.
        var compiler = new ExprCompiler(new Dictionary<string, ExprType>(StringComparer.Ordinal));

        return compiler.CompileTo(
            parameter.DefaultExpression,
            parameter.Type,
            $"The default for '{parameter.Name}'");
    }
}
