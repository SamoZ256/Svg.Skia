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

        // Such a default reaches the body through a local, so every reference — the lets below
        // included — must be emitted as that local's name.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fallback in declarations.ComputedDefaults())
        {
            names[fallback.Parameter] = fallback.Local;
        }

        var compiler = new ExprCompiler(symbols, names);

        // Held by reference, so adding here is what puts each let in scope for the ones after it.
        foreach (var let in declarations.Lets)
        {
            var (type, code) = compiler.Compile(let.Expression);
            compiled.Add((let.Name, type, code));
            symbols[let.Name] = type;
        }

        return (compiler, compiled);
    }

    /// <summary>
    /// Whether the declared defaults can be written as C# argument defaults.
    /// </summary>
    /// <remarks>
    /// C# puts optional arguments last and the declaration order is the signature's, so a document
    /// declaring a parameter with no default after one that has a default cannot have both. It keeps
    /// the order and gives up the defaults: the order is what a positional call site means and what
    /// a reader matches against the <c>&lt;e:param&gt;</c> block, and a lost default is a compile
    /// error where the caller can see it rather than a value nobody chose.
    ///
    /// Asked here rather than decided at each use, because three things read it and two of them
    /// would fail in the generated file rather than in this one.
    /// </remarks>
    public static bool EmitsDefaultArguments(this SvgExpressionDeclarations declarations)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        var optional = false;

        foreach (var parameter in declarations.Parameters)
        {
            if (parameter.DefaultExpression is { })
            {
                optional = true;
            }
            else if (optional)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One entry per parameter whose declared default cannot be a C# argument default: the
    /// parameter's own name, the local the body reads instead, and the C# it falls back to.
    /// </summary>
    /// <remarks>
    /// A C# argument default has to be a compile-time constant, and these are not:
    /// <c>new SKColor(…)</c> never is (CS1736), and a string default is one only while it stays a
    /// literal — <c>upper('a')</c> compiles to a call. Both go through a parameter defaulted to
    /// <see langword="null"/> and coalesced into a local. Computed here because <c>Resolve</c>
    /// rewrites references to those names and the generator declares them, and the two disagreeing
    /// would emit a body naming a local that does not exist.
    /// </remarks>
    public static IReadOnlyList<(string Parameter, string Local, string DefaultCode, ExprType Type)> ComputedDefaults(
        this SvgExpressionDeclarations declarations)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        // No default reaches the signature, so every parameter arrives as a value and there is
        // nothing to fall back to. Answered here so that Resolve, which asks this to know what the
        // body should name, cannot disagree with the signature about it.
        if (!declarations.EmitsDefaultArguments())
        {
            return Array.Empty<(string, string, string, ExprType)>();
        }

        var fallbacks = new List<(string, string, string, ExprType)>();
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
            if (!NeedsLocal(parameter.Type) || parameter.DefaultExpression is null)
            {
                continue;
            }

            // An author could declare `tint__default` themselves; cheap enough to rule out.
            var local = parameter.Name + "__default";
            while (!taken.Add(local))
            {
                local += "_";
            }

            fallbacks.Add((parameter.Name, local, parameter.DefaultCode()!, parameter.Type));
        }

        return fallbacks;
    }

    /// <summary>Whether a default of this type has to reach the body through a local.</summary>
    /// <remarks>
    /// A string default is sometimes a C# constant and sometimes not, and telling the two apart
    /// would mean deciding constness of emitted source. Every one takes the local instead, which
    /// costs a line in the generated method and is always right.
    /// </remarks>
    private static bool NeedsLocal(ExprType type)
        => type is ExprType.Color or ExprType.String;

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

        // Defaults may not reference other parameters: argument defaults are compile-time constants
        // in C#, and an ordering dependency between them would be invisible here.
        var compiler = new ExprCompiler(new Dictionary<string, ExprType>(StringComparer.Ordinal));

        return compiler.CompileTo(
            parameter.DefaultExpression,
            parameter.Type,
            $"The default for '{parameter.Name}'");
    }
}
