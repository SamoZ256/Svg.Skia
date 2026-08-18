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
        var compiler = new ExprCompiler(symbols);

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
