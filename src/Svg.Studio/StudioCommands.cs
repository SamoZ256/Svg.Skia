using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.Editor.Core;

namespace Svg.Studio;

/// <summary>One thing Studio can be asked to do: by a menu, by a key, or by search.</summary>
/// <remarks>
/// Ids follow upstream's <see cref="SvgEditorShortcutCatalog"/> where the meaning is the same, so a
/// command reads the same in both editors. No <see cref="Execute"/> is a key another control
/// answers, listed with the rest and run by nothing here.
/// </remarks>
public sealed record StudioCommand(string Id, string Label, string Category, Func<Task>? Execute)
{
    /// <summary>
    /// Where the key is answered. Application and Document keys are the window's; Canvas and
    /// CanvasOrDocumentTree keys stay with the control that has them, and are listed here only.
    /// </summary>
    public SvgEditorShortcutScope Scope { get; init; } = SvgEditorShortcutScope.Application;

    public SvgEditorShortcutKey? Key { get; init; }

    public SvgEditorShortcutModifiers Modifiers { get; init; }

    /// <summary>Gestures as Avalonia has them, which replace <see cref="Key"/>: undo and redo, and the palette's letters.</summary>
    public IReadOnlyList<KeyGesture>? Platform { get; init; }

    public Func<bool> CanExecute { get; init; } = static () => true;

    /// <summary>Makes the item a toggle, showing this.</summary>
    public Func<bool>? IsChecked { get; init; }

    /// <summary>The item's label while it changes, as Undo names what it would take back.</summary>
    public Func<string>? Header { get; init; }

    /// <summary>On macOS the application menu has it, so the window's menus leave it out.</summary>
    public bool InApplicationMenu { get; init; }

    /// <summary>
    /// A canvas key shown on macOS all the same. A shown gesture is a key equivalent there, which
    /// fires over every text box, so this is only for a command that routes by focus itself.
    /// </summary>
    public bool ShownOnMac { get; init; }

    /// <summary>
    /// Answered by the box being typed in, or left to it, which keeps what is typed: undo and redo
    /// take back the typing itself, and Duplicate and Delete do nothing while a box has the keyboard.
    /// </summary>
    public bool KeepsTyping { get; init; }
}

/// <summary>
/// Studio's commands, the menus built from them, the keys the window answers for them, and the
/// sheet that lists them.
/// </summary>
/// <remarks>
/// A gesture is upstream's logical one — Primary is Command on macOS and Control elsewhere — so
/// what the menus show, what the window answers and what the sheet says are worked out from one
/// declaration rather than written down three times.
/// </remarks>
public sealed class StudioCommands
{
    /// <summary>A rule between two groups in a menu.</summary>
    public const string Separator = "-";

    /// <summary>Where File → Open Recent goes; its items are filled from the list on disk.</summary>
    public const string RecentSlot = "open-recent";

    private readonly IReadOnlyList<StudioCommand> _all;
    private readonly Dictionary<string, StudioCommand> _byId = new(StringComparer.Ordinal);
    private readonly List<(StudioCommand Command, NativeMenuItem Item)> _items = new();
    private readonly List<(StudioCommand Command, Button Button)> _bound = new();
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private readonly bool _mac;

    /// <summary>Writes what is being typed into the drawing, before a command acts on it.</summary>
    public Action? CommitPending { get; init; }

    public StudioCommands(IEnumerable<StudioCommand> commands, bool mac)
    {
        _all = commands.ToList();
        _mac = mac;

        foreach (var command in _all)
        {
            if (!_byId.TryAdd(command.Id, command))
            {
                throw new ArgumentException($"Two commands are called '{command.Id}'.", nameof(commands));
            }
        }
    }

    public IReadOnlyList<StudioCommand> All => _all;

    public StudioCommand this[string id] => _byId[id];

    /// <summary>File → Open Recent, which the window fills.</summary>
    public NativeMenuItem Recent { get; } = new("Open Recent") { Menu = new NativeMenu(), IsEnabled = false };

    /// <summary>The key a logical gesture is on this platform.</summary>
    public static KeyGesture Gesture(SvgEditorShortcutKey key, SvgEditorShortcutModifiers modifiers, bool mac)
    {
        var primary = mac && !SvgEditorShortcutCatalog.UsesPhysicalControl(key, modifiers) ? KeyModifiers.Meta : KeyModifiers.Control;
        var held = KeyModifiers.None;

        if ((modifiers & SvgEditorShortcutModifiers.Primary) != 0)
        {
            held |= primary;
        }

        if ((modifiers & SvgEditorShortcutModifiers.Shift) != 0)
        {
            held |= KeyModifiers.Shift;
        }

        if ((modifiers & SvgEditorShortcutModifiers.Alt) != 0)
        {
            held |= KeyModifiers.Alt;
        }

        return new KeyGesture(ToAvalonia(key), held);
    }

    /// <summary>Every key the command answers to here, the one a menu shows first.</summary>
    public IReadOnlyList<KeyGesture> Gestures(StudioCommand command)
        => command.Platform is { Count: > 0 } platform
            ? platform
            : command.Key is { } key ? new[] { Gesture(key, command.Modifiers, _mac) } : Array.Empty<KeyGesture>();

    /// <summary>The gesture as text, spelled the way upstream's catalog spells one.</summary>
    public string Shortcut(StudioCommand command)
    {
        if (command.Platform is { Count: > 0 } platform)
        {
            return FromAvalonia(platform[0]) is var (key, modifiers)
                ? SvgEditorShortcutCatalog.FormatGesture(key, modifiers, _mac)
                : platform[0].ToString();
        }

        return command.Key is { } logical ? SvgEditorShortcutCatalog.FormatGesture(logical, command.Modifiers, _mac) : string.Empty;
    }

    /// <summary>
    /// The window's menus: each a header and the ids in it, with <see cref="Separator"/> for a rule
    /// and <see cref="RecentSlot"/> for Open Recent.
    /// </summary>
    /// <remarks>
    /// An id with no command is left out, and so is a menu left with nothing in it, so a menu whose
    /// commands come in a later change is not there until they do.
    /// </remarks>
    public NativeMenu Menu(IEnumerable<(string Header, IReadOnlyList<string> Ids)> menus)
    {
        var bar = new NativeMenu();

        foreach (var (header, ids) in menus)
        {
            var menu = new NativeMenu();

            foreach (var id in ids)
            {
                if (id == Separator)
                {
                    menu.Items.Add(new NativeMenuItemSeparator());
                }
                else if (id == RecentSlot)
                {
                    menu.Items.Add(Recent);
                }
                else if (_byId.TryGetValue(id, out var command) && !(_mac && command.InApplicationMenu))
                {
                    menu.Items.Add(Entry(command));
                }
            }

            Tidy(menu);

            if (menu.Items.Count > 0)
            {
                // On macOS, before the menu opens and before one of its key equivalents runs.
                menu.NeedsUpdate += (_, _) => Refresh();

                bar.Items.Add(new NativeMenuItem(header) { Menu = menu });
            }
        }

        Refresh();

        return bar;
    }

    /// <summary>Puts each item's enabled state, check and label back in step with what it acts on.</summary>
    public void Refresh()
    {
        foreach (var (command, item) in _items)
        {
            item.IsEnabled = command.CanExecute();

            if (command.IsChecked is { } on)
            {
                item.IsChecked = on();
            }

            if (command.Header is { } live)
            {
                item.Header = live();
            }
        }

        foreach (var (command, button) in _bound)
        {
            Sync(command, button);
        }
    }

    /// <summary>A button that runs the command and is kept in step with it as its menu item is: a toggle where the item is checkable.</summary>
    public Button Bound(string id, object? content = null)
    {
        var command = _byId[id];
        var button = command.IsChecked is { } ? new ToggleButton() : new Button();
        var shortcut = Shortcut(command);

        button.Content = content ?? command.Label;
        button.Tag = id;
        ToolTip.SetTip(button, shortcut.Length > 0 ? $"{command.Label} ({shortcut})" : command.Label);

        // Refreshed even when Run refuses: a toggle has already flipped itself by the time it is clicked.
        button.Click += async (_, _) =>
        {
            await Run(id).ConfigureAwait(true);

            Refresh();
        };

        _bound.Add((command, button));
        Sync(command, button);

        return button;
    }

    private static void Sync(StudioCommand command, Button button)
    {
        button.IsEnabled = command.CanExecute();

        if (button is ToggleButton toggle && command.IsChecked is { } on)
        {
            toggle.IsChecked = on();
        }
    }

    /// <returns>Whether the command was there to run and could.</returns>
    public async Task<bool> Run(string id)
    {
        if (!_byId.TryGetValue(id, out var command) || command.Execute is not { } execute)
        {
            return false;
        }

        // Before it is asked whether it can run: Save is only live once what is typed is in the drawing.
        if (!command.KeepsTyping)
        {
            CommitPending?.Invoke();
        }

        if (!command.CanExecute())
        {
            return false;
        }

        await execute().ConfigureAwait(true);

        Refresh();

        return true;
    }

    /// <summary>The command a key pressed in the window asks for, or null for one the window leaves alone.</summary>
    /// <param name="typing">A text box has the focus, which keeps its own undo and every key without Alt.</param>
    /// <param name="exported">macOS has the window's menu, so what it shows it answers before the window sees the key.</param>
    public StudioCommand? Resolve(KeyEventArgs e, bool typing, bool exported)
    {
        var answered = _mac && exported;

        foreach (var command in _all)
        {
            if (command.Scope is SvgEditorShortcutScope.Canvas or SvgEditorShortcutScope.CanvasOrDocumentTree)
            {
                continue;
            }

            if (typing && (command.Scope == SvgEditorShortcutScope.Document || (command.Modifiers & SvgEditorShortcutModifiers.Alt) != 0))
            {
                continue;
            }

            var gestures = Gestures(command);

            for (var i = 0; i < gestures.Count; i++)
            {
                if (answered && i == 0 && (_shown.Contains(command.Id) || command.InApplicationMenu))
                {
                    continue;
                }

                if (gestures[i].Matches(e) && command.CanExecute())
                {
                    return command;
                }
            }
        }

        return null;
    }

    /// <summary>Every command with a key, under its category, as text.</summary>
    public Control Sheet()
    {
        var page = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 4 };

        foreach (var group in _all.Where(command => Shortcut(command).Length > 0).GroupBy(command => command.Category))
        {
            page.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, page.Children.Count == 0 ? 0 : 12, 0, 2)
            });

            var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 24 };

            foreach (var command in group)
            {
                var row = rows.RowDefinitions.Count;

                rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                rows.Children.Add(Cell(command.Label, row, 0));
                rows.Children.Add(Cell(Shortcut(command), row, 1));
            }

            page.Children.Add(rows);
        }

        return new ScrollViewer { Content = page };

        static TextBlock Cell(string text, int row, int column)
        {
            var cell = new TextBlock { Text = text, HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);

            return cell;
        }
    }

    private NativeMenuItem Entry(StudioCommand command)
    {
        var item = new NativeMenuItem(command.Label) { Gesture = Shown(command) };

        if (command.IsChecked is { })
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
        }

        // Click and not Command: an item with both runs twice. What the command reads is its own
        // state and never the item's, which off macOS has already flipped itself by now.
        item.Click += async (_, _) => await Run(command.Id).ConfigureAwait(true);

        if (item.Gesture is { })
        {
            _shown.Add(command.Id);
        }

        _items.Add((command, item));

        return item;
    }

    /// <summary>The gesture a menu shows for the command, or none.</summary>
    /// <remarks>
    /// On macOS a shown gesture is a key equivalent, answered over every text box in the window, so
    /// a canvas key is shown there only for a command that routes by focus itself. Elsewhere a menu
    /// only draws the gesture.
    /// </remarks>
    private KeyGesture? Shown(StudioCommand command)
        => _mac && command.Scope is SvgEditorShortcutScope.Canvas or SvgEditorShortcutScope.CanvasOrDocumentTree && !command.ShownOnMac
            ? null
            : Gestures(command).FirstOrDefault();

    /// <summary>No rule at either end of a menu, and never two together.</summary>
    private static void Tidy(NativeMenu menu)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            var rule = menu.Items[i] is NativeMenuItemSeparator;

            if (rule && (i == 0 || i == menu.Items.Count - 1 || menu.Items[i - 1] is NativeMenuItemSeparator))
            {
                menu.Items.RemoveAt(i);
            }
        }
    }

    // Avalonia's Key is numbered as WPF's is and the catalog's as virtual keys, so no cast.
    private static Key ToAvalonia(SvgEditorShortcutKey key) => key switch
    {
        >= SvgEditorShortcutKey.A and <= SvgEditorShortcutKey.Z => Key.A + (key - SvgEditorShortcutKey.A),
        >= SvgEditorShortcutKey.Number0 and <= SvgEditorShortcutKey.Number9 => Key.D0 + (key - SvgEditorShortcutKey.Number0),
        >= SvgEditorShortcutKey.F1 and <= SvgEditorShortcutKey.F12 => Key.F1 + (key - SvgEditorShortcutKey.F1),
        SvgEditorShortcutKey.Tab => Key.Tab,
        SvgEditorShortcutKey.Comma => Key.OemComma,
        SvgEditorShortcutKey.Equals => Key.OemPlus,
        SvgEditorShortcutKey.Minus => Key.OemMinus,
        SvgEditorShortcutKey.Delete => Key.Delete,
        SvgEditorShortcutKey.Backspace => Key.Back,
        SvgEditorShortcutKey.Escape => Key.Escape,
        SvgEditorShortcutKey.Enter => Key.Enter,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No Avalonia key is mapped for it.")
    };

    private static (SvgEditorShortcutKey Key, SvgEditorShortcutModifiers Modifiers)? FromAvalonia(KeyGesture gesture)
    {
        SvgEditorShortcutKey? key = gesture.Key switch
        {
            >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => SvgEditorShortcutKey.A + (gesture.Key - Avalonia.Input.Key.A),
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => SvgEditorShortcutKey.Number0 + (gesture.Key - Avalonia.Input.Key.D0),
            _ => null
        };

        if (key is null)
        {
            return null;
        }

        var modifiers = SvgEditorShortcutModifiers.None;

        if ((gesture.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
        {
            modifiers |= SvgEditorShortcutModifiers.Primary;
        }

        if ((gesture.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            modifiers |= SvgEditorShortcutModifiers.Shift;
        }

        if ((gesture.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            modifiers |= SvgEditorShortcutModifiers.Alt;
        }

        return (key.Value, modifiers);
    }
}
