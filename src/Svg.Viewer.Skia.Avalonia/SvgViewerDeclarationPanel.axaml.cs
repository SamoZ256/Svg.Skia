// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One control per declared parameter, and one row per declared let.</summary>
public partial class SvgViewerDeclarationPanel : UserControl
{
    private const double DragThreshold = 4d;

    private readonly ItemsControl _rows;
    private readonly ItemsControl _letRows;
    private readonly TextBlock _emptyLabel;
    private readonly TextBlock _emptyLetLabel;
    private readonly StackPanel _actions;
    private readonly TextBlock _commitLabel;
    private readonly Button _commitButton;
    private readonly Button _addButton;
    private readonly Button _addLetButton;

    /// <summary>Moved rather than replaced, so a drag allocates nothing per frame.</summary>
    private readonly TranslateTransform _carry = new();

    private readonly ObservableCollection<SvgViewerLet> _lets = new();

    private IReadOnlyList<SvgViewerParameter> _parameters = Array.Empty<SvgViewerParameter>();

    /// <summary>Whether there is a drawing behind the rows, which null and empty tell apart.</summary>
    private bool _hasDocument;

    private SvgViewerLet? _pressed;
    private int _pressedFrom;
    private (int Low, int High) _window;
    private double _grabbedAt;
    private double _pressedY;
    private bool _dragging;

    public SvgViewerDeclarationPanel()
    {
        AvaloniaXamlLoader.Load(this);

        _rows = this.FindControl<ItemsControl>("Rows")!;
        _letRows = this.FindControl<ItemsControl>("LetRows")!;
        _emptyLabel = this.FindControl<TextBlock>("EmptyLabel")!;
        _emptyLetLabel = this.FindControl<TextBlock>("EmptyLetLabel")!;
        _actions = this.FindControl<StackPanel>("Actions")!;
        _commitLabel = this.FindControl<TextBlock>("CommitLabel")!;
        _commitButton = this.FindControl<Button>("CommitButton")!;
        _addButton = this.FindControl<Button>("AddButton")!;
        _addLetButton = this.FindControl<Button>("AddLetButton")!;

        _addButton.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        _commitButton.Click += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        _addLetButton.Click += (_, _) => Draft();

        _letRows.ItemsSource = _lets;

        // On the list rather than on the grip a drag starts from: capturing the list stops an event
        // ever reaching the grip again, since a captured element's events bubble past it and not
        // into it, which froze the drag at the moment it began.
        _letRows.PointerMoved += OnGripMoved;
        _letRows.PointerReleased += OnGripReleased;

        ShowActions();
    }

    /// <summary>Raised when any row's value changes.</summary>
    public event EventHandler<SvgViewerParameter>? ValueChanged;

    /// <summary>Raised when somebody asks to declare a parameter.</summary>
    /// <remarks>
    /// The panel asks rather than acts, as it does with a value: it holds rows and knows nothing
    /// about the document they came from or how one is edited.
    /// </remarks>
    public event EventHandler? AddRequested;

    /// <summary>Raised when somebody asks for the current values to become the declared defaults.</summary>
    public event EventHandler? CommitRequested;

    /// <summary>Raised when somebody asks to change what one parameter declares.</summary>
    public event EventHandler<SvgViewerParameter>? EditRequested;

    /// <summary>Raised when somebody asks to take one parameter out of the drawing.</summary>
    public event EventHandler<SvgViewerParameter>? RemoveRequested;

    /// <summary>Raised when a let row is finished with and says something the document does not.</summary>
    public event EventHandler<SvgViewerLet>? LetCommitted;

    /// <summary>Raised when a let is dragged to a new position among the lets.</summary>
    public event EventHandler<(SvgViewerLet Let, int To)>? LetMoved;

    /// <summary>The rows to show, or null when there is no drawing to declare any.</summary>
    /// <remarks>
    /// Null and empty are different answers, and the label is the whole reason to tell them apart.
    /// A drawing that declares nothing says so; a pane with no drawing behind it says nothing at
    /// all. A file that would not open has no parameters the way it has no colours, and a note that
    /// it declares none reads as a fact about the file rather than about the failure to read it.
    /// </remarks>
    public IReadOnlyList<SvgViewerParameter>? Parameters
    {
        get => _parameters;
        set
        {
            if (ReferenceEquals(_parameters, value))
            {
                // The same rows again, which is a reload that changed none of them: rebuilding the
                // items would throw away whatever row someone is part-way through editing. Only the
                // label moves, because rows being identical is not the same fact as whether there
                // is a drawing behind them.
                _emptyLabel.IsVisible = value is { Count: 0 };
                ShowActions();
                return;
            }

            Detach();

            _hasDocument = value is { };
            _parameters = value ?? Array.Empty<SvgViewerParameter>();

            foreach (var parameter in _parameters)
            {
                parameter.ValueChanged += OnRowValueChanged;
            }

            _rows.ItemsSource = _parameters;
            _emptyLabel.IsVisible = value is { Count: 0 };

            ShowActions();
        }
    }

    /// <summary>The let rows as they stand, drafts included.</summary>
    public IReadOnlyList<SvgViewerLet> Lets => _lets;

    /// <summary>Shows the lets a document declares, keeping any row still being filled in.</summary>
    /// <remarks>
    /// A rebuild is what follows every splice, and one that discarded a draft would take away the
    /// row somebody is typing into whenever anything else touched the document.
    /// </remarks>
    public void ShowLets(IReadOnlyList<SvgExpressionLet>? declared)
    {
        // A draft whose name the document now declares is one that landed, and keeping it would
        // leave the row standing beside the declaration it became.
        var drafts = _lets
            .Where(let => let.IsDraft && !Declares(declared, let.Name.Trim()))
            .ToList();

        if (Unchanged(declared, drafts.Count))
        {
            _emptyLetLabel.IsVisible = _hasDocument && _lets.Count == 0;
            return;
        }

        foreach (var let in _lets)
        {
            let.PropertyChanged -= OnLetChanged;
        }

        _lets.Clear();

        foreach (var let in declared ?? Array.Empty<SvgExpressionLet>())
        {
            Add(new SvgViewerLet(let));
        }

        foreach (var draft in drafts)
        {
            Add(draft);
        }

        _emptyLetLabel.IsVisible = _hasDocument && _lets.Count == 0;

        Validate();
    }

    public void ResetToDefaults()
    {
        foreach (var parameter in _parameters)
        {
            parameter.ResetToDefault();
        }
    }

    /// <summary>Whether these are the rows already standing, so that rebuilding them would only churn.</summary>
    /// <remarks>
    /// What is typed into a row is not part of this: a rebuild follows every splice, and one that
    /// replaced a row because somebody had edited it would take away what they were editing.
    /// </remarks>
    private bool Unchanged(IReadOnlyList<SvgExpressionLet>? declared, int drafts)
    {
        var count = declared?.Count ?? 0;

        if (_lets.Count - drafts != count)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            if (_lets[index].Declaration is not { } standing
                || !string.Equals(standing.Name, declared![index].Name, StringComparison.Ordinal)
                || !string.Equals(standing.Expression, declared[index].Expression, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Declares(IReadOnlyList<SvgExpressionLet>? declared, string name)
        => name.Length > 0
           && declared is { }
           && declared.Any(let => string.Equals(let.Name, name, StringComparison.Ordinal));

    private void Add(SvgViewerLet let)
    {
        let.PropertyChanged += OnLetChanged;
        _lets.Add(let);
    }

    /// <summary>Puts an empty row at the end and asks for the keyboard.</summary>
    private void Draft()
    {
        var draft = new SvgViewerLet(null);

        Add(draft);

        _emptyLetLabel.IsVisible = false;

        // Posted, since the container for a row added this instant has not been made yet.
        Dispatcher.UIThread.Post(
            () => (_letRows.ContainerFromIndex(_lets.Count - 1) as Control)
                ?.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault()
                ?.Focus(),
            DispatcherPriority.Background);
    }

    private void Detach()
    {
        foreach (var parameter in _parameters)
        {
            parameter.ValueChanged -= OnRowValueChanged;
        }
    }

    /// <summary>The row's own button, found through the item it was templated for.</summary>
    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SvgViewerParameter parameter })
        {
            EditRequested?.Invoke(this, parameter);
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SvgViewerParameter parameter })
        {
            RemoveRequested?.Invoke(this, parameter);
        }
    }

    private void OnRowValueChanged(object? sender, EventArgs e)
    {
        ShowActions();

        if (sender is SvgViewerParameter parameter)
        {
            ValueChanged?.Invoke(this, parameter);
        }
    }

    private void OnLetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SvgViewerLet.Name) or nameof(SvgViewerLet.Expression))
        {
            // Every row, not the one that changed: a rename puts a different name in scope for
            // everything below it.
            Validate();
        }
    }

    private void OnLetKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: SvgViewerLet let })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Settle(let);
                e.Handled = true;
                break;

            case Key.Escape:
                let.Revert();

                if (let.IsDraft)
                {
                    Discard(let);
                }

                e.Handled = true;
                break;
        }
    }

    private void OnLetLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SvgViewerLet let })
        {
            return;
        }

        // Posted, because whatever is taking the keyboard has not got it yet when this runs, and
        // tabbing from the name box to the expression box is not somebody finishing with the row.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused
                    && ReferenceEquals(focused.DataContext, let))
                {
                    return;
                }

                Settle(let);
            },
            DispatcherPriority.Background);
    }

    /// <summary>Hands a finished row over, or throws away one nothing was typed into.</summary>
    private void Settle(SvgViewerLet let)
    {
        var name = let.Name.Trim();
        var expression = let.Expression.Trim();

        if (let.IsDraft && name.Length == 0 && expression.Length == 0)
        {
            Discard(let);
            return;
        }

        if (!let.IsModified || let.HasTrouble || name.Length == 0 || expression.Length == 0)
        {
            return;
        }

        LetCommitted?.Invoke(this, let);
    }

    private void Discard(SvgViewerLet let)
    {
        let.PropertyChanged -= OnLetChanged;
        _lets.Remove(let);

        _emptyLetLabel.IsVisible = _hasDocument && _lets.Count == 0;
    }

    // ---- what the language makes of what is typed ------------------------------------------------

    /// <summary>Marks every row the language would not accept, in the scope its position gives it.</summary>
    /// <remarks>
    /// Live rather than on submit, and nothing is spliced until it checks: a half-typed body written
    /// into the drawing would stop it rendering, and the pane beside this one would show it.
    /// </remarks>
    private void Validate()
    {
        var symbols = Symbols();

        foreach (var let in _lets)
        {
            var name = let.Name.Trim();
            var expression = let.Expression.Trim();

            if (name.Length == 0 || expression.Length == 0)
            {
                // Still being filled in, which is not yet wrong.
                let.Trouble = null;
                continue;
            }

            if (symbols.ContainsKey(name))
            {
                let.Trouble = $"'{name}' is already declared.";
                continue;
            }

            try
            {
                symbols[name] = new ExprChecker(symbols).Check(expression).Type;
                let.Trouble = null;
            }
            catch (ExprException failure)
            {
                let.Trouble = failure.Message;
            }
        }
    }

    /// <summary>What is in scope before any let: the parameters, by name and type.</summary>
    private Dictionary<string, ExprType> Symbols()
    {
        var symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal);

        foreach (var parameter in _parameters)
        {
            symbols[parameter.Name] = parameter.Type;
        }

        return symbols;
    }

    /// <summary>Whether every let resolves in this order.</summary>
    private bool Resolves(IEnumerable<SvgViewerLet> order)
    {
        var symbols = Symbols();

        foreach (var let in order)
        {
            if (let.IsDraft)
            {
                // Not in the document, so it is not part of any order this could be asked about.
                continue;
            }

            try
            {
                symbols[let.Name.Trim()] = new ExprChecker(symbols).Check(let.Expression).Type;
            }
            catch (ExprException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How far the let at <paramref name="index"/> can be dragged either way.</summary>
    /// <remarks>
    /// A window rather than a check on the drop, because a refused drop reads as the drag having
    /// failed. It is contiguous: moving up is legal until the let passes what it names, and moving
    /// down until it passes what names it.
    /// </remarks>
    private (int Low, int High) Window(int index)
    {
        var low = index;
        var high = index;

        while (low > 0 && Resolves(Reordered(index, low - 1)))
        {
            low--;
        }

        while (high < _lets.Count - 1 && Resolves(Reordered(index, high + 1)))
        {
            high++;
        }

        return (low, high);
    }

    private List<SvgViewerLet> Reordered(int from, int to)
    {
        var order = _lets.ToList();
        var moved = order[from];

        order.RemoveAt(from);
        order.Insert(to, moved);

        return order;
    }

    // ---- dragging a let up or down ---------------------------------------------------------------

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SvgViewerLet let }
            || let.IsDraft
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || _lets.Count < 2)
        {
            return;
        }

        _pressed = let;
        _pressedFrom = _lets.IndexOf(let);
        _window = Window(_pressedFrom);
        _pressedY = e.GetPosition(_letRows).Y;
        _grabbedAt = _pressedY - (Row(_pressedFrom)?.Bounds.Y ?? 0d);
        _dragging = false;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } dragged)
        {
            return;
        }

        // A release the panel never saw — the button let go outside it, or over another application
        // — leaves a drag that would otherwise resume the moment the pointer comes back.
        if (!e.GetCurrentPoint(_letRows).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var y = e.GetPosition(_letRows).Y;

        if (!_dragging)
        {
            if (Math.Abs(y - _pressedY) < DragThreshold)
            {
                return;
            }

            // The list is captured, not the row: reordering takes the row out of the items, and a
            // captured control that leaves the tree loses the capture.
            _dragging = true;
            e.Pointer.Capture(_letRows);
        }

        var from = _lets.IndexOf(dragged);
        var to = from;

        for (var index = _window.Low; index <= _window.High; index++)
        {
            if (index == from || Row(index) is not { } neighbour)
            {
                continue;
            }

            // Half of a neighbour, not its edge: trading on contact leaves the pointer over the row
            // it displaced and trades straight back.
            if (index > from && y > neighbour.Bounds.Center.Y)
            {
                to = Math.Max(to, index);
            }
            else if (index < from && y < neighbour.Bounds.Center.Y)
            {
                to = Math.Min(to, index);
            }
        }

        if (Row(from) is { } carried)
        {
            carried.ZIndex = 0;
            carried.RenderTransform = null;
        }

        if (to != from)
        {
            _lets.Move(from, to);

            // The row is placed against its own laid-out position below, and a move it has not been
            // arranged for yet would put it a whole row-height off for one frame.
            _letRows.UpdateLayout();
        }

        if (Row(_lets.IndexOf(dragged)) is { } row)
        {
            row.ZIndex = 1;
            row.RenderTransform = _carry;

            _carry.Y = y - _grabbedAt - row.Bounds.Y;
        }
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e.Pointer);

    /// <summary>Puts the dragged row down where the list has already made room for it.</summary>
    private void EndDrag(IPointer? pointer)
    {
        if (_pressed is not { } dragged)
        {
            return;
        }

        var to = _lets.IndexOf(dragged);

        if (Row(to) is { } row)
        {
            row.ZIndex = 0;
            row.RenderTransform = null;
        }

        _pressed = null;
        _dragging = false;
        _carry.Y = 0d;

        pointer?.Capture(null);

        if (to != _pressedFrom && to >= 0)
        {
            LetMoved?.Invoke(this, (dragged, to));
        }
    }

    private Control? Row(int index)
        => index >= 0 && index < _lets.Count ? _letRows.ContainerFromIndex(index) as Control : null;

    /// <summary>
    /// Shows what can be done with the rows as they stand.
    /// </summary>
    /// <remarks>
    /// Nothing at all without a drawing, because adding a parameter to no document is not a thing to
    /// offer. The commit half appears only once some row differs from what the document declares:
    /// the difference is the reason to commit, so with none there is nothing to say and nothing to
    /// press.
    /// </remarks>
    private void ShowActions()
    {
        var open = _hasDocument;

        _actions.IsVisible = open;
        _addButton.IsVisible = open;
        _addLetButton.IsVisible = open;

        var changed = 0;

        if (open)
        {
            foreach (var parameter in _parameters)
            {
                if (parameter.IsModified)
                {
                    changed++;
                }
            }
        }

        _commitButton.IsVisible = changed > 0;
        _commitLabel.IsVisible = changed > 0;
        _commitLabel.Text = changed == 1
            ? "1 value differs from the declared default."
            : $"{changed} values differ from the declared defaults.";
    }
}
