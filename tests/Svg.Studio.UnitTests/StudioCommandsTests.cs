using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Svg.Editor.Core;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The registry on its own: one declaration per command, and from it the menu, the key the window
/// answers and the text the sheet shows — on macOS and elsewhere alike, whichever this runs on.
/// </summary>
public class StudioCommandsTests
{
    private const SvgEditorShortcutModifiers Primary = SvgEditorShortcutModifiers.Primary;

    private static StudioCommand Command(string id, SvgEditorShortcutScope scope = SvgEditorShortcutScope.Application) => new(id, id, "Test", () => Task.CompletedTask) { Scope = scope };

    private static IReadOnlyList<string> Headers(NativeMenuItem menu)
        => menu.Menu!.Items.Select(item => item is NativeMenuItem { Header: { } header } ? header : "-").ToList();

    [Fact]
    public void Primary_Is_Command_On_A_Mac_And_Control_Elsewhere_But_Tab_Is_Control_On_Both()
    {
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Meta), StudioCommands.Gesture(SvgEditorShortcutKey.S, Primary, mac: true));
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), StudioCommands.Gesture(SvgEditorShortcutKey.S, Primary, mac: false));
        Assert.Equal(new KeyGesture(Key.Tab, KeyModifiers.Control), StudioCommands.Gesture(SvgEditorShortcutKey.Tab, Primary, mac: true));
        Assert.Equal(new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift), StudioCommands.Gesture(SvgEditorShortcutKey.Tab, Primary | SvgEditorShortcutModifiers.Shift, mac: true));
    }

    [Fact]
    public void Settings_Is_The_Gesture_The_Application_Menu_Declares()
        => Assert.Equal(KeyGesture.Parse("Cmd+,"), StudioCommands.Gesture(SvgEditorShortcutKey.Comma, Primary, mac: true));

    // Headless, as every test that makes a StudioCommands is: its Open Recent item is an Avalonia
    // object, and one made on a pool thread between two headless tests takes the UI dispatcher from
    // the next one's setup, which then fails as a cleanup failure of whatever test it was.
    [AvaloniaFact]
    public void Two_Commands_Of_One_Name_Are_Refused()
        => Assert.Throws<ArgumentException>(() => new StudioCommands(new[] { Command("save"), Command("save") }, mac: false));

    [AvaloniaFact]
    public void A_Menu_Leaves_Out_What_Has_No_Command_And_Every_Stray_Rule()
    {
        var commands = new StudioCommands(new[] { Command("a"), Command("b") }, mac: false);

        var bar = commands.Menu(new (string, IReadOnlyList<string>)[]
        {
            ("First", new[] { "-", "a", "-", "-", "missing", "b", "-" }),
            ("Empty", new[] { "-", "missing" })
        });

        var first = Assert.Single(bar.Items.OfType<NativeMenuItem>());

        Assert.Equal("First", first.Header);
        Assert.Equal(new[] { "a", "-", "b" }, Headers(first));
    }

    [AvaloniaFact]
    public void The_Application_Menu_Has_Its_Commands_On_A_Mac_And_The_Window_Elsewhere()
    {
        var settings = Command("preferences") with { InApplicationMenu = true };
        var layout = new (string, IReadOnlyList<string>)[] { ("File", new[] { "open", "preferences" }) };

        Assert.Equal(new[] { "open" }, Headers(new StudioCommands(new[] { Command("open"), settings }, mac: true).Menu(layout).Items.OfType<NativeMenuItem>().Single()));
        Assert.Equal(new[] { "open", "preferences" }, Headers(new StudioCommands(new[] { Command("open"), settings }, mac: false).Menu(layout).Items.OfType<NativeMenuItem>().Single()));
    }

    /// <remarks>A shown gesture is a key equivalent on macOS, answered over every text box.</remarks>
    [AvaloniaFact]
    public void A_Canvas_Key_Is_Shown_On_A_Mac_Only_For_A_Command_That_Routes_Itself()
    {
        StudioCommand[] declared =
        [
            Command("zoomin", SvgEditorShortcutScope.Canvas) with { Key = SvgEditorShortcutKey.Equals, Modifiers = Primary },
            Command("duplicate", SvgEditorShortcutScope.CanvasOrDocumentTree) with { Key = SvgEditorShortcutKey.D, Modifiers = Primary, ShownOnMac = true }
        ];

        NativeMenuItem Item(bool mac, string id) => new StudioCommands(declared, mac)
            .Menu(new (string, IReadOnlyList<string>)[] { ("Edit", new[] { "zoomin", "duplicate" }) })
            .Items.OfType<NativeMenuItem>().Single().Menu!.Items.OfType<NativeMenuItem>().Single(item => item.Header == id);

        Assert.Null(Item(mac: true, "zoomin").Gesture);
        Assert.Equal(new KeyGesture(Key.D, KeyModifiers.Meta), Item(mac: true, "duplicate").Gesture);
        Assert.Equal(new KeyGesture(Key.OemPlus, KeyModifiers.Control), Item(mac: false, "zoomin").Gesture);
    }

    [AvaloniaFact]
    public async Task A_Key_Another_Control_Answers_Is_Listed_And_Not_Run()
    {
        var listed = new StudioCommand("tool-rectangle", "Rectangle", "Tools", null) { Scope = SvgEditorShortcutScope.Canvas, Key = SvgEditorShortcutKey.R };
        var commands = new StudioCommands(new[] { listed }, mac: false);

        Assert.False(await commands.Run("tool-rectangle"));
        Assert.Equal("R", commands.Shortcut(listed));
    }

    [AvaloniaFact]
    public void The_Window_Answers_Its_Own_Keys_And_Leaves_The_Canvas_Keys_Alone()
    {
        var commands = new StudioCommands(new[]
        {
            Command("save") with { Key = SvgEditorShortcutKey.S, Modifiers = Primary },
            Command("undo", SvgEditorShortcutScope.Document) with { Key = SvgEditorShortcutKey.Z, Modifiers = Primary },
            Command("tool-rectangle", SvgEditorShortcutScope.Canvas) with { Key = SvgEditorShortcutKey.R }
        }, mac: false);

        KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

        Assert.Equal("save", commands.Resolve(Press(Key.S, KeyModifiers.Control), typing: false, exported: false)?.Id);
        Assert.Null(commands.Resolve(Press(Key.R), typing: false, exported: false));

        // A box keeps its own undo, but Save is the window's wherever the caret is.
        Assert.Null(commands.Resolve(Press(Key.Z, KeyModifiers.Control), typing: true, exported: false));
        Assert.Equal("save", commands.Resolve(Press(Key.S, KeyModifiers.Control), typing: true, exported: false)?.Id);
    }

    [AvaloniaFact]
    public void An_Exported_Menu_On_A_Mac_Answers_What_It_Shows_And_Only_That()
    {
        StudioCommand[] declared =
        [
            Command("redo", SvgEditorShortcutScope.Document) with
            {
                Platform = new[] { new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift), new KeyGesture(Key.Y, KeyModifiers.Meta) }
            }
        ];

        var commands = new StudioCommands(declared, mac: true);
        commands.Menu(new (string, IReadOnlyList<string>)[] { ("Edit", new[] { "redo" }) });

        KeyEventArgs Press(Key key, KeyModifiers modifiers) => new() { Key = key, KeyModifiers = modifiers };

        Assert.Null(commands.Resolve(Press(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift), typing: false, exported: true));
        Assert.Equal("redo", commands.Resolve(Press(Key.Y, KeyModifiers.Meta), typing: false, exported: true)?.Id);
        Assert.Equal("redo", commands.Resolve(Press(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift), typing: false, exported: false)?.Id);
    }

    /// <remarks>The text is the gesture the menu shows, not the one the declaration falls back on.</remarks>
    [AvaloniaFact]
    public void A_Platform_Gesture_Is_Spelled_As_The_Catalog_Spells_One()
    {
        var redo = Command("redo") with
        {
            Key = SvgEditorShortcutKey.Z,
            Modifiers = Primary | SvgEditorShortcutModifiers.Shift,
            Platform = new[] { new KeyGesture(Key.Y, KeyModifiers.Control) }
        };

        var commands = new StudioCommands(new[] { redo }, mac: false);

        Assert.Equal("Ctrl+Y", commands.Shortcut(redo));
        Assert.Equal("Cmd+Shift+S", new StudioCommands(Array.Empty<StudioCommand>(), mac: true).Shortcut(Command("saveas") with { Key = SvgEditorShortcutKey.S, Modifiers = Primary | SvgEditorShortcutModifiers.Shift }));
    }

    /// <summary>Studio's own commands put no two on one key, on a Mac or anywhere else.</summary>
    [AvaloniaFact]
    public void No_Two_Of_Studios_Commands_Share_A_Key()
    {
        var window = new MainWindow();

        foreach (var mac in new[] { true, false })
        {
            // Every gesture, the palette's letters and the platform's undo among them.
            var commands = new StudioCommands(window.Registry.All, mac);
            var keys = commands.All.SelectMany(commands.Gestures).ToList();

            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.All(commands.All.Where(command => commands.Gestures(command).Count > 0), command => Assert.NotEmpty(commands.Shortcut(command)));
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
