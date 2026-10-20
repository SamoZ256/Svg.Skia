#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;
using Svg.Studio;
using Xunit;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// The API class the Studio project generates, against the class PaintCode generated for it:
/// every overload PaintCode has, with the same name, types and argument names in the same order.
/// </summary>
/// <remarks>
/// What a host calling PaintCode's class positionally needs to change nothing. Signatures only; the
/// drawing behind each one is <see cref="PaintCodeOracleTests"/>'s.
/// </remarks>
public class PaintCodeApiParityTests
{
    /// <summary>
    /// A dynamic frame variable (Message), and parameters their canvas declares and nothing it draws
    /// reads (Questionmark, Ventilation): a project gives the last two through a drawing's
    /// <c>parameters</c>, which this import does not write.
    /// </summary>
    private static readonly string[] s_unmatched = { "drawMessage", "drawQuestionmark", "drawVentilation" };

    [OracleFact]
    public void Every_PaintCode_Overload_Has_One_Of_The_Same_Signature()
    {
        var project = ProjectDocument.Load(OracleFactAttribute.Path!);
        var settings = new SvgcBuildSettings { TextLayout = SvgTextLayout.Relaxed };
        var loader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));

        var drawings = project.Flatten().Items
            .Select(item => SvgcProjectBuild.Build(item, settings, loader)!)
            .ToList();

        var code = SkiaCSharpCodeGen.GenerateFile(drawings, style: new SvgCodeStyle
        {
            Api = "PaintCodeResources.Api",
            Resizing = "PaintCode.ResizingBehavior",
            Context = "PaintCode.Context",
            Order = SvgcProject.ParseNames(project.Root.Order) ?? Array.Empty<string>()
        });

        var ours = Regex.Matches(code, @"public static void (draw\w+)\(([^)]*)\)")
            .Select(match => match.Groups[1].Value + "(" + string.Join(", ", match.Groups[2].Value.Split(", ").Select(Spelled)) + ")")
            .ToHashSet(StringComparer.Ordinal);

        var theirs = typeof(global::PaintCode.Context).Assembly.GetTypes()
            .Select(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name.StartsWith("draw", StringComparison.Ordinal)).ToList())
            .OrderByDescending(methods => methods.Count)
            .First();

        var unmatched = theirs
            .Where(method => !ours.Contains(method.Name + "(" + string.Join(", ", method.GetParameters().Select(Spelled)) + ")"))
            .Select(method => method.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(1380, theirs.Count);
        Assert.Equal(s_unmatched, unmatched);
    }

    private static string Spelled(string parameter) => parameter.Replace("global::PaintCode.", string.Empty).Replace("@", string.Empty);

    private static string Spelled(ParameterInfo parameter)
    {
        var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        var name = type.Name switch
        {
            "Single" => "float",
            "Boolean" => "bool",
            "String" => "string",
            "Int32" => "int",
            var other => other
        };

        return $"{(parameter.IsOut ? "out " : string.Empty)}{name} {parameter.Name}";
    }
}
#endif
