// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Model.Services;
using Svg.Skia;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>One of the templates an import can offer, or the reason it cannot be offered.</summary>
/// <param name="Recipe">The parsed template, or null where it did not parse.</param>
/// <param name="Error">Why it did not parse, for the panel to show.</param>
public sealed record TemplateEntry(string Name, SvgRecipe? Recipe, string? Error);

/// <summary>An icon on its way in, ready to be matched: its text once prepared, what is in it, and what its source said.</summary>
/// <param name="Palette">The colours in the order the source lists them, for a slot's <c>palette="n"</c>.</param>
public sealed record TemplateIcon(
    string Text,
    IReadOnlyList<SvgRecipeSurveyValue> Survey,
    string? Family,
    string? Style,
    string? Name,
    IReadOnlyList<string>? Palette)
{
    /// <summary>The icon's colours, sorted, which is what one decision covers.</summary>
    public string Signature
        => string.Join(" ", Survey.Where(value => value.Name == SvgRecipeValue.ColorName).Select(value => value.Text).OrderBy(text => text, StringComparer.Ordinal));

    /// <summary>What a choice is remembered under.</summary>
    public string ChoiceKey => $"{Family}|{Style}|{Signature}";
}

/// <summary>A template bound to one icon, and how strongly it is suggested.</summary>
/// <param name="Recipe">The bound, slot-free recipe that <see cref="SvgRecipeRewriter.Apply"/> takes.</param>
/// <param name="Sure">The first suggestion was remembered or leads by 3 or more; anything else wants checking.</param>
public sealed record TemplateSuggestion(TemplateEntry Template, SvgRecipe Recipe, double Score, bool Sure);

/// <summary>One drawing on its way into a group.</summary>
/// <param name="Text">The drawing as <see cref="TemplateLibrary.Prepare"/> left it.</param>
/// <param name="Recipe">A bound template, whose size and declarations come with it, or null to keep the drawing as it is.</param>
/// <param name="Source">What <see cref="ProjectDrawing.Source"/> says, such as <c>streamline:&lt;hash&gt;</c>.</param>
public sealed record TemplateImport(string Name, string Text, SvgRecipe? Recipe = null, string? Source = null);

/// <summary>The project's import templates, and which of them suits an incoming icon.</summary>
public sealed class TemplateLibrary
{
    public const string KeepColoursName = "Keep colours";

    private static readonly TemplateEntry s_keepColours = new(
        KeepColoursName,
        SvgRecipe.Parse($"<recipe xmlns=\"{SvgRecipe.Namespace}\" name=\"{KeepColoursName}\" />"),
        null);

    private static readonly Regex s_currentColor = new(@"(?<=(?:^|;)\s*(?<name>[\w-]+)\s*:\s*)currentColor\b", RegexOptions.IgnoreCase);

    // After '{' as well as ';', so one pattern reads a style attribute and a <style> sheet.
    private static readonly Regex s_saysFill = new(@"(^|[;{])\s*fill\s*:");

    private readonly ProjectRoot _root;

    public TemplateLibrary(ProjectRoot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));

        Templates = root.Templates
            .Select(template =>
            {
                try
                {
                    return new TemplateEntry(template.Name, SvgRecipe.Parse(template.Text), null);
                }
                catch (SvgRecipeException failure)
                {
                    return new TemplateEntry(template.Name, null, failure.Message);
                }
            })
            .Append(s_keepColours)
            .ToList();
    }

    /// <summary>
    /// Where the choices somebody made are kept, as <c>family|style|colours=template</c> lines.
    /// </summary>
    /// <remarks>Settable so a test drives a file of its own instead of the one on this machine.</remarks>
    public static string ChoicesStore { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Svg.Studio",
        "templates-choices");

    /// <summary>The project's templates in file order, those that did not parse among them, then <see cref="KeepColoursName"/>.</summary>
    public IReadOnlyList<TemplateEntry> Templates { get; }

    public static string? Remembered(TemplateIcon icon) => StudioSettings.Read(icon.ChoiceKey, ChoicesStore);

    public static void Remember(TemplateIcon icon, string template)
    {
        StudioSettings.Write(icon.ChoiceKey, template, ChoicesStore);

        // Under no style too, which is how an update asks: a drawing does not keep what it was searched under.
        if (icon.Style is { })
        {
            StudioSettings.Write((icon with { Style = null }).ChoiceKey, template, ChoicesStore);
        }
    }

    /// <summary>
    /// The icon's text with the paint a template cannot see made literal: <c>currentColor</c> as
    /// <c>#000000</c>, and a root <c>fill="#000000"</c> where nothing says fill at all.
    /// </summary>
    /// <remarks>
    /// The root fill is the default made explicit, so it draws the same, but a survey only counts
    /// what is written. A drawing that says fill anywhere keeps its implicit ones.
    /// </remarks>
    public static TemplateIcon Prepare(
        string svgText,
        string? family = null,
        string? style = null,
        string? name = null,
        IReadOnlyList<string>? palette = null)
    {
        var source = SvgSourceDocument.Read(svgText, out var refusal) ?? throw new SvgRecipeException(refusal!);
        var root = source.Document.Root!;

        foreach (var attribute in root.DescendantsAndSelf().Attributes())
        {
            if (attribute.Name.LocalName == "style")
            {
                // A declaration is surveyed as much as an attribute, so it is made literal as well.
                var styled = s_currentColor.Replace(attribute.Value, found => IsColor(found.Groups["name"].Value) ? "#000000" : found.Value);

                if (styled != attribute.Value)
                {
                    attribute.Value = styled;
                }
            }
            else if (IsColor(attribute.Name.LocalName)
                     && string.Equals(attribute.Value.Trim(), "currentColor", StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = "#000000";
            }
        }

        // A <style> sheet counts: a root fill under CSS that paints would be surveyed, bound and never drawn.
        if (!root.DescendantsAndSelf().Any(element =>
                element.Attribute("fill") is { }
                || (element.Attribute("style") is { } declared && s_saysFill.IsMatch(declared.Value))
                || (element.Name.LocalName == "style" && s_saysFill.IsMatch(element.Value))))
        {
            root.SetAttributeValue("fill", "#000000");
        }

        var text = source.ToText();

        return new TemplateIcon(text, SvgRecipeRewriter.Survey(text), family, style, name, palette);

        static bool IsColor(string attribute) => SvgExpressionAttributes.TypeFor(attribute) == ExprType.Color;
    }

    /// <summary>A batch split so one decision serves each part: the icons of one family with the same colours.</summary>
    public static IEnumerable<IGrouping<(string? Family, string Signature), TemplateIcon>> Batch(IEnumerable<TemplateIcon> icons)
        => icons.GroupBy(icon => (icon.Family, icon.Signature));

    /// <summary>The templates that fit <paramref name="icon"/> once bound, best first, with <see cref="KeepColoursName"/> last.</summary>
    /// <param name="target">The group the icon is going into, whose declarations a template need not bring.</param>
    public IReadOnlyList<TemplateSuggestion> Suggest(TemplateIcon icon, ProjectGroup target)
    {
        var declared = ProjectDeclarations.Names(target);
        var remembered = Remembered(icon);

        var bound = new List<(TemplateEntry Template, SvgRecipe Recipe, double Score)>();

        foreach (var template in Templates)
        {
            if (template.Recipe is not { } recipe || !recipe.Matches(icon.Survey, icon.Family, icon.Style, icon.Name))
            {
                continue;
            }

            var binding = recipe.Bind(icon.Survey, icon.Palette, declared);

            if (binding.Recipe is { } fitted)
            {
                bound.Add((template, fitted, Score(recipe, fitted, binding.Leftover.Count) + (template.Name == remembered ? 10 : 0)));
            }
        }

        var priors = Priors(bound.Select(one => one.Recipe).ToList(), target);

        var ranked = bound
            .Select((one, index) => one with { Score = one.Score + priors[index] })
            .Where(one => one.Template != s_keepColours)
            .OrderByDescending(one => one.Score)
            .ToList();

        // Last whatever it scores, unless it is what was chosen for icons like this one before.
        var keep = bound.Single(one => one.Template == s_keepColours);
        ranked.Insert(remembered == KeepColoursName ? 0 : ranked.Count, keep);

        var lead = ranked.Count == 1 ? double.PositiveInfinity : ranked[0].Score - ranked.Skip(1).Max(one => one.Score);
        var sure = ranked[0].Template.Name == remembered || lead >= 3;

        return ranked
            .Select((one, index) => new TemplateSuggestion(one.Template, one.Recipe, one.Score, index == 0 && sure))
            .ToList();

        double Score(SvgRecipe recipe, SvgRecipe fitted, int leftover)
        {
            var match = recipe.Match;
            var weight = new[] { match?.Colors, match?.Strokes, match?.Fills, match?.Family, match?.Style, match?.Name, match?.Has }.Count(set => set is { });

            // A rest slot takes whatever is left, so claiming a colour says nothing about the template's fit.
            var claimed = fitted.Rules.Count(rule =>
                rule.Name == SvgRecipeValue.ColorName
                && rule.Slot is not { Rest: true }
                && icon.Survey.Any(value => value.Name == SvgRecipeValue.ColorName && value.Text == rule.Key));

            return weight + claimed - (2 * leftover);
        }
    }

    /// <summary>
    /// For each bound recipe, the share of drawings already writing one of its expressions: in the
    /// target group, or across the project where the group writes none of them.
    /// </summary>
    /// <remarks>A share rather than a count, so it breaks a tie between templates and never outweighs a whole point.</remarks>
    private double[] Priors(IReadOnlyList<SvgRecipe> recipes, ProjectGroup target)
    {
        var written = recipes
            .Select(recipe => recipe.Rules.Select(rule => "{{ " + rule.Expression + " }}").Distinct().ToList())
            .ToList();

        foreach (var scope in new[] { target, _root })
        {
            var texts = scope.Drawings.Select(drawing => drawing.Text).ToList();

            var shares = written
                .Select(expressions => texts.Count == 0
                    ? 0d
                    : texts.Count(text => expressions.Any(expression => text.Contains(expression, StringComparison.Ordinal))) / (double)texts.Count)
                .ToArray();

            if (shares.Any(share => share > 0d))
            {
                return shares;
            }
        }

        return new double[recipes.Count];
    }

    /// <summary>
    /// Adds <paramref name="imports"/> to <paramref name="target"/> from <paramref name="index"/> on,
    /// with the declarations their templates need and the target does not inherit, as one undo step.
    /// </summary>
    /// <param name="notes">A drawing that was left out and why, or a name two templates declare differently.</param>
    /// <returns>The drawings added, in order; none where the declarations could not be written.</returns>
    public static IReadOnlyList<ProjectDrawing> Import(
        ProjectWorkspace workspace,
        ProjectGroup target,
        int index,
        IReadOnlyList<TemplateImport> imports,
        ICollection<string> notes)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        // Nothing left to add is no step at all, rather than one that ⌘Z takes back to no effect.
        if (Prepared(workspace, target, imports, notes) is not { Texts.Count: > 0 } prepared)
        {
            return Array.Empty<ProjectDrawing>();
        }

        var added = new List<ProjectDrawing>();

        workspace.Do(
            prepared.Texts.Count == 1 ? $"add {prepared.Texts[0].Import.Name}" : $"add {prepared.Texts.Count} drawings",
            // The block is one of the group's own nodes, so this puts the declarations back as well.
            () => ProjectSnapshot.Contents(target),
            () =>
            {
                if (prepared.Code is { } code && target.SetCode(code) is { } refusal)
                {
                    notes.Add(refusal);

                    return;
                }

                foreach (var (import, text) in prepared.Texts)
                {
                    try
                    {
                        var drawing = target.AddDrawing(import.Name, text, index++);

                        drawing.Source = import.Source;
                        added.Add(drawing);
                    }
                    catch (SvgcProjectException failure)
                    {
                        notes.Add($"{import.Name}: {failure.Message}");
                    }
                }
            });

        return added;
    }

    /// <summary>
    /// Puts <paramref name="import"/> in place of what <paramref name="drawing"/> draws, keeping its
    /// name, place and source, with any declarations it needs, as one undo step.
    /// </summary>
    /// <returns>Whether it was replaced; <paramref name="notes"/> says why not.</returns>
    public static bool Replace(ProjectWorkspace workspace, ProjectDrawing drawing, TemplateImport import, ICollection<string> notes)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        if (drawing?.Parent is not { } group
            || Prepared(workspace, group, new[] { import }, notes) is not { Texts: [var (_, text)] } prepared)
        {
            return false;
        }

        string? refusal = null;

        workspace.Do(
            $"update {ProjectWorkspace.Label(drawing)}",
            () => prepared.Code is { }
                ? ProjectSnapshot.All(ProjectSnapshot.Code(group), ProjectSnapshot.Text(drawing))
                : ProjectSnapshot.Text(drawing),
            () =>
            {
                // The text first: it can be refused where the block, checked in Prepared, cannot, and
                // a refusal before anything is written leaves no half of the update behind.
                // Indented as AddDrawing indents, since the text arrives written for a file of its own.
                refusal = drawing.Inline(text, indent: true);

                if (refusal is null && prepared.Code is { } code)
                {
                    refusal = group.SetCode(code);
                }
            });

        if (refusal is { })
        {
            notes.Add(refusal);
        }

        return refusal is null;
    }

    /// <summary>
    /// Puts <paramref name="templateText"/> in place of the project's template at <paramref name="index"/>,
    /// after the last where the index is their count, or takes that one out where the text is null,
    /// as one undo step.
    /// </summary>
    /// <returns>Why it could not, or null.</returns>
    /// <remarks>
    /// A template that does not parse as a recipe is still kept, and listed with its error, as one
    /// written into the file by hand would be; only what the block cannot hold is refused.
    /// </remarks>
    public static string? Put(ProjectWorkspace workspace, int index, string? templateText)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        var root = workspace.Document.Root;
        var block = SvgSourceDocument.Read(root.TemplatesText, out _)!;
        var holder = block.Document.Root!;
        var templates = holder.Elements().ToList();
        var was = index < templates.Count ? Named(templates[index]) : null;
        string label;

        if (templateText is null)
        {
            if (templates[index].PreviousNode is XText indent && indent.Value.Trim().Length == 0)
            {
                indent.Remove();
            }

            templates[index].Remove();
            label = $"remove template {was}";
        }
        else
        {
            var read = SvgSourceDocument.Read(templateText.Replace("\r\n", "\n"), out var unreadable);

            if (read is null)
            {
                return unreadable;
            }

            var template = read.Document.Root!;
            var name = Named(template);

            if (template.Name != (XNamespace)SvgRecipe.Namespace + "recipe")
            {
                return $"A template has to be an <e:recipe xmlns:e=\"{SvgRecipe.Namespace}\">.";
            }

            if (name.Length == 0)
            {
                return "A template needs a name, which is what an import offers it as.";
            }

            if (name == KeepColoursName || templates.Where((_, at) => at != index).Any(other => Named(other) == name))
            {
                return $"The project already has a template called '{name}'.";
            }

            // Detached first, for the reason ProjectDrawing.Inline is.
            template.Remove();

            if (index < templates.Count)
            {
                templates[index].ReplaceWith(template);
                label = was == name ? $"edit template {name}" : $"rename template {was} to {name}";
            }
            else
            {
                // Beside the last one, or one level in from the close where there is none; a block
                // made from the empty one is moved to its depth when it goes in, as a new <e:code> is.
                var closing = holder.LastNode as XText;
                var indent = templates.LastOrDefault()?.PreviousNode is XText before && before.Value.Contains('\n')
                    ? before.Value
                    : (closing?.Value ?? "\n") + block.IndentUnit;

                ProjectGroup.Reindent(template, string.Empty, indent.Substring(indent.LastIndexOf('\n') + 1));

                if (closing is { })
                {
                    closing.AddBeforeSelf(new XText(indent), template);
                }
                else
                {
                    holder.Add(new XText(indent), template, new XText("\n"));
                }

                label = $"add template {name}";
            }

            // The block binds the prefix already, and a template read on its own carries it too.
            foreach (var declared in template.Attributes().Where(one => one.IsNamespaceDeclaration).ToList())
            {
                if (holder.GetNamespaceOfPrefix(declared.Name.LocalName)?.NamespaceName == declared.Value)
                {
                    declared.Remove();
                }
            }
        }

        var text = block.ToText();

        // Nothing changed is no step, rather than one that undoes to no effect.
        if (text == root.TemplatesText)
        {
            return null;
        }

        string? refusal = null;

        workspace.Do(label, () => ProjectSnapshot.Templates(root), () => refusal = root.SetTemplates(text));

        return refusal;

        static string Named(XElement template) => ((string?)template.Attribute("name"))?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// A template that writes <paramref name="drawingText"/>'s <c>{{ }}</c> paints into a drawing
    /// laid out like it: one slot per expression, picked by where it paints and how much.
    /// </summary>
    /// <remarks>No <c>&lt;code&gt;</c>: the names the expressions use are the drawing's group's, and an import leans on them there.</remarks>
    /// <exception cref="SvgRecipeException">The drawing is unreadable, or paints nothing through an expression.</exception>
    public static string Extract(string name, string drawingText)
    {
        // In one survey, as a binding meets the colours they stand for: ties are ranked in that order.
        var uses = SvgRecipeRewriter.Survey(drawingText, written: true)
            .Where(value => value.Name == SvgRecipeValue.ColorName)
            .Select(value => new Use(SvgExpressionAttributes.TryUnwrap(value.Text, out var expression) ? expression : null, value, null))
            .ToList();

        return Written(name, uses, "coverage", like: null, family: null);
    }

    /// <summary>
    /// A template that does to icons like <paramref name="icon"/> what <paramref name="mapping"/>
    /// does to it, picking each colour by where it paints and how light it is.
    /// </summary>
    /// <param name="mapping">The bound recipe an import row would apply, whose size, padding and declarations come along.</param>
    /// <param name="family">A family the template is kept to, or null for any.</param>
    /// <exception cref="SvgRecipeException"><paramref name="mapping"/> turns no colour into an expression.</exception>
    public static string Save(string name, TemplateIcon icon, SvgRecipe? mapping, string? family)
    {
        var uses = icon.Survey
            .Where(value => value.Name == SvgRecipeValue.ColorName)
            .Select(value => mapping?.Rules.FirstOrDefault(rule => rule.Name == SvgRecipeValue.ColorName && rule.Key == value.Text) is { } rule
                ? new Use(rule.Expression, value, rule.Slot?.Name)
                : new Use(null, value, null))
            .ToList();

        return Written(name, uses, "lightness", mapping, family);
    }

    /// <summary>One colour of the drawing a template is written from, and what it becomes there, or null for staying as it is.</summary>
    private sealed record Use(string? Expression, SvgRecipeSurveyValue Value, string? Slot);

    /// <summary>
    /// Slots that pick out each use in turn, as <see cref="SvgRecipeSlot"/> will bind them: what it
    /// paints, where only one attribute, and its place among what is still unclaimed.
    /// </summary>
    /// <remarks>
    /// A rank counts what is left when the slot binds, so it is worked out against that rather than
    /// the whole drawing. The last expression takes the rest, unless a colour is to stay as it is,
    /// which a rest slot would take as well.
    /// </remarks>
    private static string Written(string name, IReadOnlyList<Use> uses, string by, SvgRecipe? like, string? family)
    {
        if (!uses.Any(use => use.Expression is { }))
        {
            throw new SvgRecipeException("Nothing here is painted through an expression, so there is nothing for a template to write.");
        }

        var ns = (XNamespace)SvgRecipe.Namespace;
        var recipe = Framed(like, new XAttribute(XNamespace.Xmlns + "e", SvgRecipe.Namespace), new XAttribute("name", name));

        recipe.Add(new XElement(
            ns + "match",
            new XAttribute("colors", uses.Count),
            new XAttribute("strokes", uses.Count(use => use.Value.Strokes > 0)),
            family is { } ? new XAttribute("family", family) : null));

        if (like?.Declarations is { Count: > 0 } declarations)
        {
            recipe.Add(new XElement(ns + "code", declarations.Select(declaration => new XElement(declaration))));
        }

        var left = uses.ToList();
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var use in uses.Where(use => use.Expression is { }).OrderByDescending(use => use.Value.Count))
        {
            if (left.All(other => other.Expression == use.Expression))
            {
                recipe.Add(new XElement(ns + "slot", new XAttribute("name", Unique(use.Slot ?? "rest")), new XAttribute("rest", "true"), use.Expression));

                break;
            }

            var paint = use.Value.Attributes.Count == 1 ? use.Value.Attributes.Keys.Single() : null;
            var candidates = left.Where(other => paint is null || other.Value.Attributes.ContainsKey(paint));
            var ranked = by == "lightness"
                ? candidates.OrderBy(other => SvgRecipeColor.Luminance(SvgRecipeColor.Parse(other.Value.Text, other.Value.Text)))
                : candidates.OrderByDescending(other => other.Value.Count);

            recipe.Add(new XElement(
                ns + "slot",
                new XAttribute("name", Unique(use.Slot ?? paint ?? "colour")),
                paint is { } ? new XAttribute("paint", paint) : null,
                new XAttribute("by", by),
                new XAttribute("rank", ranked.ToList().IndexOf(use) + 1),
                use.Expression));

            left.Remove(use);
        }

        return recipe.ToString();

        string Unique(string slot)
        {
            var unique = slot;

            for (var n = 2; !named.Add(unique); n++)
            {
                unique = $"{slot} {n}";
            }

            return unique;
        }
    }

    /// <summary>A <c>&lt;recipe&gt;</c> holding <paramref name="content"/>, at <paramref name="like"/>'s size and padding.</summary>
    internal static XElement Framed(SvgRecipe? like, params object[] content)
    {
        var recipe = new XElement((XNamespace)SvgRecipe.Namespace + "recipe", content);

        if (like?.Size is { } size)
        {
            recipe.SetAttributeValue("size", size.ToString(CultureInfo.InvariantCulture));
        }

        if (like is { Padding: { IsEmpty: false } padding })
        {
            recipe.SetAttributeValue(
                "padding",
                string.Join(" ", new[] { padding.Top, padding.Right, padding.Bottom, padding.Left }.Select(side => side.ToString(CultureInfo.InvariantCulture))));
        }

        return recipe;
    }

    /// <summary>Which of <paramref name="sources"/> a drawing under <paramref name="group"/> already came from.</summary>
    public static IReadOnlySet<string> Imported(ProjectGroup group, IEnumerable<string> sources)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        var present = group.Drawings.Select(drawing => drawing.Source).OfType<string>().ToHashSet(StringComparer.Ordinal);

        return sources.Where(present.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Each import's text with its template applied, and the target's block with what they need
    /// added; null where that block could not take it.
    /// </summary>
    private static (string? Code, List<(TemplateImport Import, string Text)> Texts)? Prepared(
        ProjectWorkspace workspace,
        ProjectGroup target,
        IReadOnlyList<TemplateImport> imports,
        ICollection<string> notes)
    {
        var declared = ProjectDeclarations.Names(target);
        var needed = new List<XElement>();
        var disagreed = new HashSet<string>(StringComparer.Ordinal);
        var texts = new List<(TemplateImport, string)>();

        foreach (var import in imports)
        {
            if (import.Recipe is not { } recipe)
            {
                texts.Add((import, import.Text));

                continue;
            }

            try
            {
                // Bound again against every name it declares, which keeps the rules and drops the
                // declarations: those go on the group, and a drawing redeclaring a name is refused.
                var bare = recipe.Bind(Array.Empty<SvgRecipeSurveyValue>(), declared: recipe.Declarations.Select(Named)).Recipe ?? recipe;

                texts.Add((import, SvgRecipeRewriter.Apply(Sized(import.Text, recipe), bare).Svg));
            }
            catch (SvgRecipeException failure)
            {
                notes.Add($"{import.Name}: {failure.Message}");

                continue;
            }

            foreach (var declaration in recipe.Declarations.Where(one => !declared.Contains(Named(one))))
            {
                if (needed.FirstOrDefault(one => Named(one) == Named(declaration)) is not { } first)
                {
                    needed.Add(declaration);
                }
                else if (!ProjectPlacement.Same(first, declaration) && disagreed.Add(Named(declaration)))
                {
                    notes.Add($"Two templates declare '{Named(declaration)}' differently, so the first one's was kept.");
                }
            }
        }

        if (needed.Count == 0)
        {
            return (null, texts);
        }

        if (Declare(workspace, target, needed, out var code) is { } refusal)
        {
            notes.Add(refusal);

            return null;
        }

        return (code, texts);

        static string Named(XElement declaration) => ((string?)declaration.Attribute("name"))?.Trim() ?? string.Empty;
    }

    /// <summary>The target's block with <paramref name="needed"/> written into it, or the refusal.</summary>
    /// <remarks>Through the group panel's own path, so what it writes is placed and checked as a declaration typed there would be.</remarks>
    private static string? Declare(ProjectWorkspace workspace, ProjectGroup target, IReadOnlyList<XElement> needed, out string code)
    {
        SvgExpressionDeclarations adding;

        code = string.Empty;

        try
        {
            // Copies, since adding a parentless element adopts it and these belong to the recipe.
            adding = SvgExpressionDeclarations.Parse(
                new XElement((XNamespace)SvgRecipe.Namespace + "code", needed.Select(one => new XElement(one))).ToString());
        }
        catch (ExprException failure)
        {
            return failure.Message;
        }

        return new GroupTarget(workspace, target).Edited(
            source =>
            {
                foreach (var parameter in adding.Parameters)
                {
                    if (SvgDeclarationEditor.Add(source, parameter) is { } refusal)
                    {
                        return $"'{parameter.Name}' could not be declared on {ProjectWorkspace.Label(target)}: {refusal}";
                    }
                }

                foreach (var let in adding.Lets)
                {
                    if (SvgDeclarationEditor.AddLet(source, let.Name, let.Expression, let.DeclaredType) is { } refusal)
                    {
                        return $"'{let.Name}' could not be declared on {ProjectWorkspace.Label(target)}: {refusal}";
                    }
                }

                return null;
            },
            out code);
    }

    /// <summary>
    /// The icon mapped onto the square <paramref name="recipe"/> asks for, or as it is where it asks
    /// for none or the icon has no frame to map from.
    /// </summary>
    /// <remarks>
    /// The viewBox becomes <c>0 0 size size</c> and the content moves into a <c>&lt;g transform&gt;</c>,
    /// rather than the viewBox being reframed as a resize does, so every drawing of a set shares one
    /// coordinate space. Where the content lands is <see cref="SvgSceneSizing.Frame"/>'s answer, as for svgc.
    /// </remarks>
    public static string Sized(string svgText, SvgRecipe recipe)
    {
        if (recipe.Size is not { } size
            || SvgService.FromSvg(svgText) is not { } own
            || SvgSourceDocument.Read(svgText, out _) is not { Document.Root: { } root } source)
        {
            return svgText;
        }

        // An absent width reads as SVG.NET's 100%, and a relative or physical one is no frame in user units either.
        var content = own.ViewBox.Width > 0f && own.ViewBox.Height > 0f
            ? ShimSkiaSharp.SKRect.Create(own.ViewBox.MinX, own.ViewBox.MinY, own.ViewBox.Width, own.ViewBox.Height)
            : IsUserUnits(own.Width) && IsUserUnits(own.Height)
                ? ShimSkiaSharp.SKRect.Create(0f, 0f, own.Width.Value, own.Height.Value)
                : ShimSkiaSharp.SKRect.Empty;

        if (content.Width <= 0f || content.Height <= 0f)
        {
            return svgText;
        }

        var frame = SvgSceneSizing.Frame(content, new ShimSkiaSharp.SKSize(size, size), recipe.Padding);
        var scale = size / frame.Width;
        var x = 0f - (frame.MinX * scale);
        var y = 0f - (frame.MinY * scale);

        var transform = (x == 0f && y == 0f ? string.Empty : $"translate({Number(x)} {Number(y)}) ")
                        + $"scale({Number(scale)})";

        // Detached first, so each keeps the text it was read as; see ProjectDrawing.Inline.
        var nodes = root.Nodes().ToList();
        nodes.ForEach(node => node.Remove());
        root.Add(new XElement(root.Name.Namespace + "g", new XAttribute("transform", transform), nodes));

        SvgFrameEditor.SetFrame(source, size.ToSvgString(), size.ToSvgString(), $"0 0 {size.ToSvgString()} {size.ToSvgString()}");

        return source.ToText();

        // Fewer digits than float carries, because the frame's round trip turns a scale of 1 into 0.9999999.
        static string Number(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

        static bool IsUserUnits(SvgUnit unit) => unit.Type is SvgUnitType.User or SvgUnitType.Pixel;
    }
}
