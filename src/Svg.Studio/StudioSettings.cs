// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Styling;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>Which way round the editor is painted.</summary>
public enum StudioTheme
{
    /// <summary>Whatever the machine is set to, and it changes with it.</summary>
    System,

    /// <summary>Light, whatever the machine is set to.</summary>
    Light,

    /// <summary>Dark, whatever the machine is set to.</summary>
    Dark
}

/// <summary>What an export does about text it cannot write out as the author drives it.</summary>
public enum RelaxedTextAnswer
{
    /// <summary>Ask, every time there is something to ask about.</summary>
    Ask,

    /// <summary>Always relax, without asking.</summary>
    Always,

    /// <summary>Never relax: the default text is written in, and the export says which.</summary>
    Never
}

/// <summary>
/// What the editor has been told to do differently, kept between sessions.
/// </summary>
/// <remarks>
/// One file of <c>key=value</c> lines beside the list of what was opened lately, read and written on
/// the spot the way <see cref="RecentFiles"/> is — a second window sees what the first one changed.
/// Shaped on that class rather than sharing it: a capped list of paths has nothing in it to widen.
/// </remarks>
public static class StudioSettings
{
    private const string AutosaveKey = "autosave";

    private const string CaptionSizeKey = "captionSize";

    private const string DrawingCaptionsKey = "drawingCaptions";

    private const string ShowInvisibleKey = "showInvisible";

    private const string RelaxedTextKey = "relaxedText";

    private const string ThemeKey = "theme";

    private const string SnapToGridKey = "snapToGrid";

    private const string ShowGridKey = "showGrid";

    private const string RulersKey = "rulers";

    private const string GuidesKey = "guides";

    private const string SmartGuidesKey = "smartGuides";

    private const string GroundKey = "ground";

    private const string GridSizeKey = "gridSize";

    private const string RotationStepKey = "rotationStep";

    private const string LayoutKey = "layout";

    private const string AssistantProviderKey = "assistantProvider";

    private const string AssistantModelKey = "assistantModel";

    private const string McpEnabledKey = "mcpEnabled";

    private const string McpPortKey = "mcpPort";

    private const string ConvertAsksKey = "convertAsks";

    private const string DropAsksKey = "dropAsks";

    private const string ConvertIntegersKey = "convertIntegers";

    private const string ConvertOrganizesKey = "convertOrganizes";

    /// <summary>Moves <see cref="Folder"/>, and keeps Studio off the keychain.</summary>
    /// <remarks>
    /// How the UI tests keep a launched Studio off its user's files: .NET's macOS ApplicationData
    /// does not follow <c>HOME</c>.
    /// </remarks>
    public const string FolderVariable = "SVG_STUDIO_DATA";

    /// <summary>The folder everything Studio keeps on this machine is in.</summary>
    public static string Folder { get; } = Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } moved
        ? moved
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Svg.Studio");

    /// <summary>
    /// Where the settings are kept.
    /// </summary>
    /// <remarks>Settable so a test drives a file of its own instead of the one on this machine.</remarks>
    public static string Store
    {
        get => s_store;
        set
        {
            s_store = value;

            // Outline is the session's and never the file's, and another file is another session.
            Outline = false;
        }
    }

    private static string s_store = Path.Combine(Folder, "settings");

    /// <summary>Whether a recovery copy is kept of the project being edited.</summary>
    /// <remarks>
    /// On unless the file says otherwise, so a missing file, a missing line, a value nobody can read
    /// and a file that cannot be read at all all leave it on. Nothing switches it off by accident:
    /// the only way there is the line the toggle writes.
    /// </remarks>
    public static bool Autosave
    {
        get => !string.Equals(Read(AutosaveKey), "off", StringComparison.Ordinal);
        set => Write(AutosaveKey, value ? "on" : "off");
    }

    /// <summary>Which way round the editor is painted.</summary>
    /// <remarks>
    /// The machine's own answer unless the file says otherwise, for the reason the recovery copy
    /// defaults on: a line nobody can read is not an answer. It is also the only one of the three
    /// that goes on being right — somebody whose machine turns dark at sunset has said what they
    /// want once, and the editor follows.
    /// </remarks>
    public static StudioTheme Theme
    {
        get => Read(ThemeKey) switch
        {
            "light" => StudioTheme.Light,
            "dark" => StudioTheme.Dark,
            _ => StudioTheme.System
        };

        set => Write(ThemeKey, value switch
        {
            StudioTheme.Light => "light",
            StudioTheme.Dark => "dark",
            _ => "system"
        });
    }

    /// <summary>
    /// The theme as Avalonia spells it.
    /// </summary>
    /// <remarks>
    /// <see cref="ThemeVariant.Default"/> is the one that follows the machine, which is why nothing
    /// here asks the platform what it is set to: the variant already does, and it goes on doing it
    /// while the editor is open.
    ///
    /// Here rather than beside the one line that assigns it, because that line is in <c>App</c> —
    /// the one class the suite cannot reach, since it builds an <c>Application</c> of its own.
    /// </remarks>
    public static ThemeVariant Variant => Theme switch
    {
        StudioTheme.Light => ThemeVariant.Light,
        StudioTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    /// <summary>Whether an export relaxes the text layout, or asks each time.</summary>
    /// <remarks>
    /// Ask unless the file says otherwise, for the reason the recovery copy defaults on: an answer
    /// nobody can read is not an answer, and what this decides is what a drawing comes out looking
    /// like. It only ever comes up for a project holding text an expression drives, so a set of
    /// icons without any is never asked at all.
    /// </remarks>
    public static RelaxedTextAnswer RelaxedText
    {
        get => Read(RelaxedTextKey) switch
        {
            "on" => RelaxedTextAnswer.Always,
            "off" => RelaxedTextAnswer.Never,
            _ => RelaxedTextAnswer.Ask
        };

        set => Write(RelaxedTextKey, value switch
        {
            RelaxedTextAnswer.Always => "on",
            RelaxedTextAnswer.Never => "off",
            _ => "ask"
        });
    }

    /// <summary>How big a name on a board is drawn, in control pixels.</summary>
    /// <remarks>
    /// A caption is chrome rather than part of a drawing, so how big is readable is about the screen
    /// somebody is at — which is the one thing neither the canvas nor the project can work out.
    ///
    /// Anything the file cannot be read as, and anything outside what the canvas will take, comes
    /// back as the default rather than as a refusal: a settings file is not something to fail over.
    /// </remarks>
    public static double CaptionSize
    {
        get => Read(
            CaptionSizeKey,
            SvgViewerCanvas.DefaultCaptionSize,
            SvgViewerCanvas.MinimumCaptionSize,
            SvgViewerCanvas.MaximumCaptionSize);

        set => Write(CaptionSizeKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Whether each drawing on a board is named under it.</summary>
    /// <remarks>
    /// On unless the file says otherwise, the way the recovery copy is: a board that names what is
    /// on it is the one somebody reading a project wants, and a name is written inside the drawing
    /// it belongs to, so a board of them is laid out exactly like a board without.
    /// </remarks>
    public static bool DrawingCaptions
    {
        get => !string.Equals(Read(DrawingCaptionsKey), "off", StringComparison.Ordinal);
        set => Write(DrawingCaptionsKey, value ? "on" : "off");
    }

    /// <summary>Whether what a drawing has but does not paint is outlined over it.</summary>
    /// <remarks>On unless the file says otherwise: none of it paints, so hidden it is nowhere.</remarks>
    public static bool ShowInvisible
    {
        get => !string.Equals(Read(ShowInvisibleKey), "off", StringComparison.Ordinal);
        set => Write(ShowInvisibleKey, value ? "on" : "off");
    }

    /// <summary>Whether opening a PaintCode document asks how to convert it, or converts it as last asked.</summary>
    /// <remarks>On unless somebody ticked the box that says not to ask again.</remarks>
    public static bool ConvertAsks
    {
        get => !string.Equals(Read(ConvertAsksKey), "off", StringComparison.Ordinal);
        set => Write(ConvertAsksKey, value ? "on" : "off");
    }

    /// <summary>Whether Streamline icons dropped into a project ask which template to use, or import a sure match straight away.</summary>
    /// <remarks>On unless somebody ticked the box that says not to ask again; an unsure match asks either way.</remarks>
    public static bool DropAsks
    {
        get => !string.Equals(Read(DropAsksKey), "off", StringComparison.Ordinal);
        set => Write(DropAsksKey, value ? "on" : "off");
    }

    /// <summary>Whether a PaintCode conversion writes whole numbers as integers.</summary>
    /// <remarks>Off, the conversion's own default, until a conversion is asked for with it on.</remarks>
    public static bool ConvertIntegers
    {
        get => string.Equals(Read(ConvertIntegersKey), "on", StringComparison.Ordinal);
        set => Write(ConvertIntegersKey, value ? "on" : "off");
    }

    /// <summary>Whether a PaintCode conversion organizes the variables it gives each drawing.</summary>
    /// <remarks>On, the conversion's own default, until a conversion is asked for with it off.</remarks>
    public static bool ConvertOrganizes
    {
        get => !string.Equals(Read(ConvertOrganizesKey), "off", StringComparison.Ordinal);
        set => Write(ConvertOrganizesKey, value ? "on" : "off");
    }

    /// <summary>Whether a gesture lands on the grid rather than where the pointer stopped.</summary>
    /// <remarks>
    /// Off unless the file says otherwise, which is the other way round from the recovery copy: a
    /// drag that does not write what was dragged is a surprise, and nobody who has not asked for a
    /// grid should meet one.
    /// </remarks>
    public static bool SnapToGrid
    {
        get => string.Equals(Read(SnapToGridKey), "on", StringComparison.Ordinal);
        set => Write(SnapToGridKey, value ? "on" : "off");
    }

    /// <summary>Whether the grid's lines are drawn, which is apart from whether anything lands on them.</summary>
    /// <remarks>Off unless the file says otherwise, as <see cref="SnapToGrid"/> is.</remarks>
    public static bool ShowGrid
    {
        get => string.Equals(Read(ShowGridKey), "on", StringComparison.Ordinal);
        set => Write(ShowGridKey, value ? "on" : "off");
    }

    /// <summary>Whether every canvas has rulers along its top and left.</summary>
    public static bool Rulers
    {
        get => string.Equals(Read(RulersKey), "on", StringComparison.Ordinal);
        set => Write(RulersKey, value ? "on" : "off");
    }

    /// <summary>Whether the guides dragged out of the rulers are shown, and landed on.</summary>
    public static bool Guides
    {
        get => !string.Equals(Read(GuidesKey), "off", StringComparison.Ordinal);
        set => Write(GuidesKey, value ? "on" : "off");
    }

    /// <summary>Whether a drag lands on the other shapes' edges and centres.</summary>
    public static bool SmartGuides
    {
        get => !string.Equals(Read(SmartGuidesKey), "off", StringComparison.Ordinal);
        set => Write(SmartGuidesKey, value ? "on" : "off");
    }

    /// <summary>Whether the drawings are shown as their outlines alone, for this session only.</summary>
    /// <remarks>Never written down, so no launch opens on a drawing gone grey without being asked.</remarks>
    public static bool Outline { get; set; }

    /// <summary>What is behind the drawings on every canvas: the theme's, a checker, light or dark.</summary>
    public static SvgViewerGround Ground
    {
        get => Enum.TryParse<SvgViewerGround>(Read(GroundKey), ignoreCase: true, out var ground) && Enum.IsDefined(ground) ? ground : SvgViewerGround.Theme;
        set => Write(GroundKey, value.ToString().ToLowerInvariant());
    }

    /// <summary>How far apart the grid lines are, in the space the drawings are arranged in.</summary>
    /// <remarks>
    /// Read the way a caption size is, and for the same reason: a settings file is not something to
    /// fail over, so anything that cannot be read and anything out of range is the default.
    /// </remarks>
    public static double GridSize
    {
        get => Read(GridSizeKey, SvgViewerGrid.DefaultStep, SvgViewerGrid.MinimumStep, SvgViewerGrid.MaximumStep);
        set => Write(GridSizeKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether a size was ever chosen, as against the one a drawing is given for having none.
    /// </summary>
    /// <remarks>
    /// The number alone cannot say: a file with no line and a file saying ten read the same. What
    /// hangs on it is whether a drawing gets a step of its own — see <see cref="SvgViewerGrid.For"/>.
    /// </remarks>
    public static bool HasGridSize => Read(GridSizeKey) is { };

    /// <summary>How far apart the angles a turn lands on are, in degrees.</summary>
    /// <remarks>Its own number because a grid has no angle. See <see cref="SvgViewerGrid"/>.</remarks>
    public static double RotationStep
    {
        get => Read(RotationStepKey, SvgViewerGrid.DefaultTurn, SvgViewerGrid.MinimumTurn, SvgViewerGrid.MaximumTurn);
        set => Write(RotationStepKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// How the panels are arranged around a drawing, as <see cref="SvgViewerDock"/> writes it.
    /// </summary>
    /// <remarks>
    /// One arrangement for every tab, because arranging the strip once should arrange it everywhere:
    /// a drawing's tab and a group's board are the same four panels in the same body.
    ///
    /// Stored as it was written and handed back the same way — this is the one setting whose meaning
    /// is somebody else's. What a line this reads back cannot say is the dock's to answer, and it
    /// answers by falling back whole to its own default, so nothing here has to know the grammar.
    /// </remarks>
    public static string Layout
    {
        get => Read(LayoutKey) is { Length: > 0 } written ? written : DefaultLayout;
        set => Write(LayoutKey, value ?? DefaultLayout);
    }

    /// <summary>Which provider the assistant talks to, by its id, or null for the first one usable.</summary>
    public static string? AssistantProvider
    {
        get => Read(AssistantProviderKey);
        set => Write(AssistantProviderKey, value ?? string.Empty);
    }

    /// <summary>Which of the provider's models the assistant talks to, by its id.</summary>
    public static string? AssistantModel
    {
        get => Read(AssistantModelKey);
        set => Write(AssistantModelKey, value ?? string.Empty);
    }

    /// <summary>Whether Claude Code may connect to this Studio, through <see cref="StudioMcpServer"/>.</summary>
    /// <remarks>Off unless the file says on: it opens a port, which nothing should do unasked.</remarks>
    public static bool McpEnabled
    {
        get => string.Equals(Read(McpEnabledKey), "on", StringComparison.Ordinal);
        set => Write(McpEnabledKey, value ? "on" : "off");
    }

    public const int DefaultMcpPort = 7337;

    /// <summary>The port the server listens on, on this machine only.</summary>
    public static int McpPort
    {
        get => (int)Read(McpPortKey, DefaultMcpPort, 1024, 65535);
        set => Write(McpPortKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>What a window nobody has arranged comes up in: the Drawing layout, UnoDraw's shell on Studio's panels.</summary>
    /// <remarks>
    /// Every layout names the panels still to come too, which take no room until something supplies
    /// them, so the dock never has to find a panel a place of its own. Streamline is folded under the
    /// drawing, which otherwise gets a fifth of a 1180×760 window; its tab opens it.
    /// </remarks>
    public const string DefaultLayout =
        "row(col(tree+changes+history/1.4/tree/open,variables+project/1/variables/open)/280px,col(*/1.6,streamline/1/streamline/folded)/1,"
        + "col(element+transform+align/1.5/element/open,elements+resources+brushes+assistant/1/elements/open)/312px)";

    /// <summary>For a group's board: Streamline under the tree, and the board the whole height of the window.</summary>
    public const string BoardLayout =
        "row(col(tree+changes+history/1.4/tree/open,streamline/1/streamline/open)/280px,*/1,"
        + "col(variables+project/1/variables/open,element+elements+transform+align+resources+brushes+assistant/1.5/element/open)/312px)";

    /// <summary>For going over what changed: Changes and History wide on the left, Streamline folded away.</summary>
    public const string ReviewLayout =
        "row(changes+history+tree/360px/changes/open,col(*/1.6,streamline/1/streamline/folded)/1,"
        + "col(variables+project/1/variables/open,elements+element+transform+align+resources+brushes+assistant/1.5/elements/open)/312px)";

    /// <summary>The layouts the control bar's picker and the Window menu offer, by name.</summary>
    /// <remarks>They differ in shape and not only in size, because that is all the dock compares; see <see cref="SvgViewerDock.Matches"/>.</remarks>
    public static IReadOnlyList<(string Name, string Line)> Layouts { get; } = new[]
    {
        ("Drawing", DefaultLayout),
        ("Board", BoardLayout),
        ("Review", ReviewLayout)
    };

    /// <summary>The two steps as one, which is what a canvas and a gesture are handed.</summary>
    /// <remarks>
    /// The steps whether or not <see cref="SnapToGrid"/> is on, so switching it off and on again is
    /// the grid somebody set rather than the one it started at. Here rather than at each of the
    /// places that push it onto a tab, so a board, a drawing tab and the one a test builds cannot
    /// come to disagree about what the settings say.
    /// </remarks>
    public static SvgViewerGrid Grid => new((float)GridSize, (float)RotationStep);

    /// <summary>A number the file holds, or the default where it holds nothing usable.</summary>
    private static double Read(string key, double fallback, double least, double most)
        => double.TryParse(Read(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && value >= least
           && value <= most
            ? value
            : fallback;

    /// <summary>A line of <paramref name="store"/>, or of <see cref="Store"/> where none is named.</summary>
    internal static string? Read(string key, string? store = null)
    {
        store ??= Store;

        try
        {
            if (!File.Exists(store))
            {
                return null;
            }

            return File.ReadAllLines(store)
                .Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2 && string.Equals(pair[0], key, StringComparison.Ordinal))
                .Select(pair => pair[1])
                .LastOrDefault();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string key, string value, string? store = null)
    {
        store ??= Store;

        try
        {
            var lines = (File.Exists(store) ? File.ReadAllLines(store) : Array.Empty<string>())
                .Where(line => !line.StartsWith(key + "=", StringComparison.Ordinal))
                .Append($"{key}={value}");

            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            File.WriteAllLines(store, lines);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A preference that could not be written comes back as what it is by default, which is
            // where it was before anybody touched it.
        }
    }
}
