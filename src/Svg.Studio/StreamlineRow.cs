// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>An icon as Streamline served it, and as a template sees it.</summary>
internal sealed record StreamlineDownload(StreamlineIcon Icon, string Svg, TemplateIcon Prepared);

/// <summary>
/// One decision in an import: icons of one family with the same colours, the template they go in
/// through, and what each colour becomes.
/// </summary>
public sealed class StreamlineRow
{
    public const string Keep = "keep";

    private readonly IReadOnlyList<StreamlineDownload> _icons;
    private readonly ProjectGroup _target;
    private readonly IReadOnlyList<string> _names;

    /// <summary>The colours somebody has given a role by hand, and null for one they kept.</summary>
    private readonly Dictionary<string, string?> _roles = new(StringComparer.Ordinal);

    private IReadOnlyList<IReadOnlyList<TemplateSuggestion>> _suggestions = Array.Empty<IReadOnlyList<TemplateSuggestion>>();
    private string _chosen = TemplateLibrary.KeepColoursName;

    internal StreamlineRow(IReadOnlyList<StreamlineDownload> icons, TemplateLibrary library, ProjectGroup target)
    {
        _icons = icons;
        _target = target;
        _names = ColourNames(target);

        Suggest(library, null);
    }

    public IReadOnlyList<StreamlineIcon> Icons => _icons.Select(icon => icon.Icon).ToList();

    /// <summary>The eligible templates, suggested first, and Keep colours where the suggestion puts it.</summary>
    public IReadOnlyList<string> Choices { get; private set; } = Array.Empty<string>();

    public string Suggested => Choices[0];

    /// <summary>Nothing changed by hand from a first suggestion that was remembered or well ahead of the next.</summary>
    public bool Sure => _roles.Count == 0 && _chosen == Choices[0] && _suggestions[0].FirstOrDefault()?.Sure == true;

    /// <summary>The template the icons go in through; choosing one starts its colours over.</summary>
    public string Chosen
    {
        get => _chosen;
        set
        {
            if (!Choices.Contains(value))
            {
                throw new ArgumentException($"'{value}' is not offered for these icons.", nameof(value));
            }

            _chosen = value;
            _roles.Clear();
        }
    }

    /// <summary>The first icon's colours, each of which <see cref="Role"/> says what becomes of.</summary>
    public IReadOnlyList<string> Colours
        => _icons[0].Prepared.Survey.Where(one => one.Name == SvgRecipeValue.ColorName).Select(one => one.Text).ToList();

    /// <summary>The names the declarations each drawing brings, across the row.</summary>
    public IReadOnlyList<string> Declares => Enumerable.Range(0, _icons.Count)
        .Select(index => Recipe(index))
        .OfType<SvgRecipe>()
        .SelectMany(recipe => recipe.Declarations)
        .Select(declaration => ((string?)declaration.Attribute("name"))?.Trim())
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .ToList();

    internal IReadOnlyList<StreamlineDownload> Downloads => _icons;

    /// <summary>What <paramref name="colour"/> can become: <see cref="Keep"/>, what the template writes, or a colour the target names.</summary>
    public IReadOnlyList<string> RoleChoices(string colour)
    {
        var roles = new List<string> { Keep };

        // An expression the template writes that is not a bare name is still what the colour becomes, so it is offered too.
        if (Colour(Bound(), colour)?.Expression is { } written && !_names.Contains(written))
        {
            roles.Add(written);
        }

        roles.AddRange(_names);

        return roles;
    }

    /// <summary>What <paramref name="colour"/> becomes: <see cref="Keep"/> or an expression.</summary>
    public string Role(string colour) => Colour(Recipe(0), colour)?.Expression ?? Keep;

    public void SetRole(string colour, string role) => _roles[colour] = role == Keep ? null : role;

    /// <summary>Where <paramref name="colour"/>'s role comes from and what it is, as a caption under it.</summary>
    public string RoleNote(string colour)
    {
        var rule = Colour(Recipe(0), colour);

        // The slot is the template's, and an overridden recipe is slot-free, so it is read from the template.
        var slot = _roles.ContainsKey(colour) ? "by hand" : Colour(Bound(), colour)?.Slot?.Name;

        return rule is null ? "kept" : slot is { } ? $"{slot} → {rule.Expression}" : $"→ {rule.Expression}";
    }

    /// <summary>What the icon at <paramref name="index"/> goes in through, or null to go in as it is.</summary>
    public SvgRecipe? Recipe(int index)
    {
        var bound = Recipe(_suggestions[index], _chosen);

        return _roles.Count == 0 ? bound : Overridden(bound);
    }

    /// <summary>The recipe of the suggestion called <paramref name="chosen"/>, or null for Keep colours or one not suggested.</summary>
    internal static SvgRecipe? Recipe(IEnumerable<TemplateSuggestion> suggestions, string? chosen)
        => chosen == TemplateLibrary.KeepColoursName
            ? null
            : suggestions.FirstOrDefault(suggestion => suggestion.Template.Name == chosen)?.Recipe;

    /// <summary>The icon at <paramref name="index"/> as it will be imported, or through <paramref name="template"/> as it stands.</summary>
    /// <exception cref="SvgRecipeException">The template cannot be applied to the icon.</exception>
    public string Text(int index, string? template = null)
    {
        var text = _icons[index].Prepared.Text;

        return (template is null ? Recipe(index) : Recipe(_suggestions[index], template)) is { } recipe
            ? SvgRecipeRewriter.Apply(TemplateLibrary.Sized(text, recipe), recipe).Svg
            : text;
    }

    /// <summary>Keeps the choice for icons like these, which is what makes it sure the next time.</summary>
    /// <remarks>Not where a colour was given a role by hand, which the template alone would not bring back.</remarks>
    public void Remember()
    {
        if (_roles.Count > 0)
        {
            return;
        }

        foreach (var icon in _icons.Select(one => one.Prepared).DistinctBy(one => one.ChoiceKey))
        {
            TemplateLibrary.Remember(icon, _chosen);
        }
    }

    /// <summary>
    /// Suggests again from <paramref name="library"/>, keeping <paramref name="chosen"/> where it is
    /// still offered, and the roles set by hand where their colour can still take them.
    /// </summary>
    internal void Suggest(TemplateLibrary library, string? chosen)
    {
        _suggestions = _icons.Select(icon => library.Suggest(icon.Prepared, _target)).ToList();
        Choices = _suggestions[0].Select(suggestion => suggestion.Template.Name).ToList();

        if (chosen is null || !Choices.Contains(chosen))
        {
            Chosen = Choices[0];

            return;
        }

        _chosen = chosen;

        foreach (var (colour, role) in _roles.ToList())
        {
            if (role is { } && !RoleChoices(colour).Contains(role))
            {
                _roles.Remove(colour);
            }
        }
    }

    private SvgRecipe? Bound() => Recipe(_suggestions[0], _chosen);

    /// <summary>
    /// The bound template with the colours given a role by hand written as rules of their own: an
    /// ordinary slot-free recipe, which the import applies as it would any other, bringing only the
    /// declarations its rules still reach.
    /// </summary>
    private SvgRecipe? Overridden(SvgRecipe? bound)
    {
        var ns = (XNamespace)SvgRecipe.Namespace;
        var root = TemplateLibrary.Framed(bound);
        var rules = (bound?.Rules ?? Array.Empty<SvgReplaceRule>())
            .Where(rule => rule.Name != SvgRecipeValue.ColorName || !_roles.ContainsKey(rule.Key))
            .Select(rule => (rule.Name, Value: rule.ValueText, rule.Expression))
            .Concat(_roles.Where(role => role.Value is { }).Select(role => (Name: SvgRecipeValue.ColorName, Value: role.Key, Expression: role.Value!)))
            .ToList();

        var declarations = Reached(bound?.Declarations ?? Array.Empty<XElement>(), rules.Select(rule => rule.Expression));

        if (declarations.Count > 0)
        {
            root.Add(new XElement(ns + "code", declarations.Select(declaration => new XElement(declaration))));
        }

        foreach (var (name, value, expression) in rules)
        {
            root.Add(new XElement(ns + "replace", new XAttribute(name, value), expression));
        }

        return rules.Count > 0 ? SvgRecipe.Parse(root.ToString()) : null;
    }

    /// <summary>The declarations <paramref name="expressions"/> name, and those they name in turn.</summary>
    /// <remarks>By whole word, which can keep one a string literal happens to spell, never drop one that is used.</remarks>
    private static List<XElement> Reached(IReadOnlyList<XElement> declarations, IEnumerable<string> expressions)
    {
        var texts = expressions.ToList();
        var kept = new List<XElement>();

        for (var more = true; more;)
        {
            more = false;

            foreach (var declaration in declarations.Except(kept).ToList())
            {
                if (((string?)declaration.Attribute("name"))?.Trim() is { Length: > 0 } name
                    && texts.Any(text => Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(name)}(?!\w)")))
                {
                    kept.Add(declaration);
                    texts.Add(declaration.ToString());
                    more = true;
                }
            }
        }

        return declarations.Where(kept.Contains).ToList();
    }

    private static SvgReplaceRule? Colour(SvgRecipe? recipe, string colour)
        => recipe?.Rules.FirstOrDefault(rule => rule.Name == SvgRecipeValue.ColorName && rule.Key == colour);

    /// <summary>A drawing on its way into <paramref name="target"/>, read through the declarations it will inherit there, or null where it cannot be.</summary>
    internal static SvgViewerDocument? Drawn(string text, ProjectGroup? target)
    {
        try
        {
            return SvgViewerDocument.LoadFromSvg(text, null, default, target is { } ? own => ProjectDeclarations.Built(target, own) : null);
        }
        catch (Exception)
        {
            // Anything, as a board's drawing: this is a download reaching a parser, and a preview
            // that cannot be drawn is no reason to refuse the row.
            return null;
        }
    }

    /// <summary>The colour names a drawing added to <paramref name="target"/> can use, in the order they are declared.</summary>
    private static IReadOnlyList<string> ColourNames(ProjectGroup target)
    {
        var scope = ProjectDeclarations.Scope(target);
        var checker = ExprChecker.For(scope);

        return scope.Parameters.Select(parameter => parameter.Name)
            .Concat(scope.Lets.Select(let => let.Name))
            .Where(name =>
            {
                try
                {
                    return checker.Check(name).Type == ExprType.Color;
                }
                catch (ExprException)
                {
                    return false;
                }
            })
            .ToList();
    }
}

/// <summary>What an import window decides: icons on their way into a group, one row per decision.</summary>
public sealed class StreamlineImport
{
    internal StreamlineImport(ProjectWorkspace workspace, ProjectGroup target, IReadOnlyList<StreamlineDownload> downloads, ProjectDrawing? updating = null)
    {
        Workspace = workspace;
        Target = target;
        Updating = updating;

        var library = new TemplateLibrary(workspace.Document.Root);

        Rows = TemplateLibrary.Batch(downloads.Select(download => download.Prepared))
            .Select(batch => new StreamlineRow(batch.Select(icon => downloads.First(one => ReferenceEquals(one.Prepared, icon))).ToList(), library, target))
            .ToList();
    }

    public ProjectGroup Target { get; }

    public IReadOnlyList<StreamlineRow> Rows { get; }

    /// <summary>The drawing an update replaces, or null where the icons are new.</summary>
    public ProjectDrawing? Updating { get; }

    internal ProjectWorkspace Workspace { get; }

    /// <summary>Suggests again from the project's templates as they are now, a row's choice following <paramref name="renamed"/>.</summary>
    public void Refresh((string From, string To)? renamed = null)
    {
        var library = new TemplateLibrary(Workspace.Document.Root);

        foreach (var row in Rows)
        {
            row.Suggest(library, renamed is { } rename && row.Chosen == rename.From ? rename.To : row.Chosen);
        }
    }

    /// <summary>Every icon as the rows import it.</summary>
    internal IReadOnlyList<TemplateImport> Imports()
        => Rows.SelectMany(row => row.Downloads.Select((download, index) =>
                new TemplateImport(download.Icon.Name, download.Prepared.Text, row.Recipe(index), StreamlinePanel.SourceOf(download.Icon))))
            .ToList();
}
