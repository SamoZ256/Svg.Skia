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
    private readonly ItemsControl _rows;
    private readonly ItemsControl _letRows;
    private readonly TextBlock _emptyLabel;
    private readonly TextBlock _emptyLetLabel;
    private readonly StackPanel _actions;
    private readonly TextBlock _commitLabel;
    private readonly Button _commitButton;
    private readonly Button _addButton;
    private readonly Button _addLetButton;

    private readonly ObservableCollection<SvgViewerLet> _lets = new();
    private readonly ObservableCollection<SvgViewerParameter> _parameters = new();

    private readonly RowDrag<SvgViewerLet> _letDrag;
    private readonly RowDrag<SvgViewerParameter> _parameterDrag;

    /// <summary>The rows as they were handed over, so the same list twice can be recognised.</summary>
    private IReadOnlyList<SvgViewerParameter>? _source;

    /// <summary>Whether there is a drawing behind the rows, which null and empty tell apart.</summary>
    private bool _hasDocument;

    private bool _canDeclare = true;

    /// <summary>The last edit handed to the document, so the same one is not handed over twice.</summary>
    /// <remarks>
    /// A row goes on calling itself modified until the rebuild its own edit caused replaces it, and
    /// the box it was in leaving the tree is a focus loss — so without this, committing with Enter
    /// and then clicking away wrote the edit again. The second write names what the first one
    /// renamed away, or declares what it had just added. Never cleared: it is one tuple, and
    /// clearing it on the rebuild would drop the guard exactly when it is needed.
    /// </remarks>
    private (SvgViewerLet Let, string Name, string Expression)? _handed;

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

        _rows.ItemsSource = _parameters;
        _letRows.ItemsSource = _lets;

        _letDrag = new RowDrag<SvgViewerLet>(_letRows, _lets, LetWindow, (let, to) => LetMoveRequested?.Invoke(let, to) == true);
        _parameterDrag = new RowDrag<SvgViewerParameter>(
            _rows, _parameters, ParameterWindow, (row, to) => ParameterMoveRequested?.Invoke(row, to) == true);

        ShowActions();
    }

    /// <summary>
    /// Whether what the drawing declares may be changed here. Values are settable either way.
    /// </summary>
    /// <remarks>
    /// Off when the declarations are not in the drawing: an svgc project applying a recipe puts the
    /// parameters in the recipe file, and every command here writes into the drawing's own text —
    /// which would also give it a declaration block of its own, and a recipe refuses a document
    /// that already has one. The rows stay, because binding values to them is the point of showing
    /// a recipe's parameters at all.
    /// </remarks>
    public bool CanDeclare
    {
        get => _canDeclare;
        set
        {
            if (_canDeclare == value)
            {
                return;
            }

            _canDeclare = value;

            // A class, because the per-row buttons and grips are made by templates as rows appear
            // and there is nothing here to reach them through.
            Classes.Set("locked", !value);

            ShowActions();
        }
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

    /// <summary>Asked when a let is dragged to a new position, and answers whether it landed.</summary>
    /// <remarks>
    /// A function rather than an event, because this one needs an answer: a splice can decline for
    /// reasons the drag could not have known, and a list left showing an order the drawing does not
    /// have is worse than a drag that does not land. Refused, the row goes back where it was.
    /// </remarks>
    public Func<SvgViewerLet, int, bool>? LetMoveRequested { get; set; }

    /// <summary>Asked when a parameter is dragged to a new position, on the same terms.</summary>
    public Func<SvgViewerParameter, int, bool>? ParameterMoveRequested { get; set; }

    /// <summary>Raised when somebody asks to take one let out of the drawing.</summary>
    public event EventHandler<SvgViewerLet>? LetRemoveRequested;

    /// <summary>The rows to show, or null when there is no drawing to declare any.</summary>
    /// <remarks>
    /// Null and empty are different answers, and the label is the whole reason to tell them apart.
    /// A drawing that declares nothing says so; a pane with no drawing behind it says nothing at
    /// all. A file that would not open has no parameters the way it has no colours, and a note that
    /// it declares none reads as a fact about the file rather than about the failure to read it.
    /// </remarks>
    public IReadOnlyList<SvgViewerParameter>? Parameters
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value))
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
            _source = value;

            _parameters.Clear();

            foreach (var parameter in value ?? Array.Empty<SvgViewerParameter>())
            {
                parameter.ValueChanged += OnRowValueChanged;
                _parameters.Add(parameter);
            }

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

    /// <summary>A row nothing has been written for yet is thrown away rather than asked about.</summary>
    private void OnRemoveLetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SvgViewerLet let })
        {
            return;
        }

        if (let.IsDraft)
        {
            Discard(let);
            return;
        }

        LetRemoveRequested?.Invoke(this, let);
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

        if (_handed is { } last
            && ReferenceEquals(last.Let, let)
            && string.Equals(last.Name, name, StringComparison.Ordinal)
            && string.Equals(last.Expression, expression, StringComparison.Ordinal))
        {
            return;
        }

        _handed = (let, name, expression);

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

    // ---- dragging a row up or down ---------------------------------------------------------------

    /// <summary>Starts a drag on whichever list the grip belongs to.</summary>
    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: { } row } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        switch (row)
        {
            // A row nobody has written yet is not in the document, so there is no order to move it in.
            case SvgViewerLet { IsDraft: false } let:
                _letDrag?.Press(let, e);
                break;

            case SvgViewerParameter parameter:
                _parameterDrag?.Press(parameter, e);
                break;
        }
    }

    /// <summary>How far the let at <paramref name="index"/> can be dragged either way.</summary>
    /// <remarks>
    /// A window rather than a check on the drop, because a refused drop reads as the drag having
    /// failed. It is contiguous: moving up is legal until the let passes what it names, and moving
    /// down until it passes what names it.
    /// </remarks>
    private (int Low, int High) LetWindow(int index)
        => Window(index, _lets.Count, to => Resolves(Reordered(_lets, index, to)));

    /// <summary>How far the parameter at <paramref name="index"/> can be dragged: anywhere.</summary>
    /// <remarks>
    /// Unlike a let, whose position is what it can name, a parameter's is presentation. A back end
    /// may want them in some order of its own — the C# generator needs the ones with defaults last
    /// — and says so when it is run, rather than a drawing being unable to say what it means.
    /// </remarks>
    private (int Low, int High) ParameterWindow(int index) => (0, _parameters.Count - 1);

    /// <summary>The run of positions around <paramref name="index"/> that <paramref name="legal"/> allows.</summary>
    private static (int Low, int High) Window(int index, int count, Func<int, bool> legal)
    {
        var low = index;
        var high = index;

        while (low > 0 && legal(low - 1))
        {
            low--;
        }

        while (high < count - 1 && legal(high + 1))
        {
            high++;
        }

        return (low, high);
    }

    private static List<T> Reordered<T>(IReadOnlyList<T> rows, int from, int to)
    {
        var order = rows.ToList();
        var moved = order[from];

        order.RemoveAt(from);
        order.Insert(to, moved);

        return order;
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

    /// <summary>
    /// Dragging a row up and down a list.
    /// </summary>
    /// <remarks>
    /// One implementation for both lists, because the second one wanted the same four details the
    /// first had to find: capture the list and not the row, since reordering takes the row out of
    /// the items and a captured control that leaves the tree loses the capture; swap at a
    /// neighbour's midpoint, since trading on contact leaves the pointer over the row it displaced
    /// and trades straight back; treat a release nobody saw as an end; and lay out before placing
    /// the carried row, or it sits a whole row-height off for a frame.
    /// </remarks>
    private sealed class RowDrag<T>
        where T : class
    {
        private const double Threshold = 4d;

        private readonly ItemsControl _items;
        private readonly ObservableCollection<T> _rows;
        private readonly Func<int, (int Low, int High)> _window;

        /// <summary>Puts the move to the document, which may decline it.</summary>
        private readonly Func<T, int, bool> _dropped;

        /// <summary>Moved rather than replaced, so a drag allocates nothing per frame.</summary>
        private readonly TranslateTransform _carry = new();

        private T? _pressed;
        private int _from;
        private (int Low, int High) _allowed;
        private double _grabbedAt;
        private double _pressedY;
        private bool _dragging;

        public RowDrag(
            ItemsControl items,
            ObservableCollection<T> rows,
            Func<int, (int Low, int High)> window,
            Func<T, int, bool> dropped)
        {
            _items = items;
            _rows = rows;
            _window = window;
            _dropped = dropped;

            _items.PointerMoved += OnMoved;
            _items.PointerReleased += (_, e) => End(e.Pointer);
        }

        public void Press(T row, PointerPressedEventArgs e)
        {
            if (_rows.Count < 2)
            {
                return;
            }

            _pressed = row;
            _from = _rows.IndexOf(row);
            _allowed = _window(_from);
            _pressedY = e.GetPosition(_items).Y;
            _grabbedAt = _pressedY - (Row(_from)?.Bounds.Y ?? 0d);
            _dragging = false;
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_pressed is not { } dragged)
            {
                return;
            }

            // A release the panel never saw — the button let go outside it, or over another
            // application — leaves a drag that would otherwise resume when the pointer comes back.
            if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed)
            {
                End(e.Pointer);
                return;
            }

            var y = e.GetPosition(_items).Y;

            if (!_dragging)
            {
                if (Math.Abs(y - _pressedY) < Threshold)
                {
                    return;
                }

                _dragging = true;
                e.Pointer.Capture(_items);
            }

            var from = _rows.IndexOf(dragged);
            var to = from;

            for (var index = _allowed.Low; index <= _allowed.High; index++)
            {
                if (index == from || Row(index) is not { } neighbour)
                {
                    continue;
                }

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
                _rows.Move(from, to);
                _items.UpdateLayout();
            }

            if (Row(_rows.IndexOf(dragged)) is { } row)
            {
                row.ZIndex = 1;
                row.RenderTransform = _carry;

                _carry.Y = y - _grabbedAt - row.Bounds.Y;
            }
        }

        /// <summary>Puts the dragged row down where the list has already made room for it.</summary>
        private void End(IPointer? pointer)
        {
            if (_pressed is not { } dragged)
            {
                return;
            }

            var to = _rows.IndexOf(dragged);

            if (Row(to) is { } row)
            {
                row.ZIndex = 0;
                row.RenderTransform = null;
            }

            _pressed = null;
            _dragging = false;
            _carry.Y = 0d;

            pointer?.Capture(null);

            if (to == _from || to < 0)
            {
                return;
            }

            // Put back where it was if the document would not take it. The window keeps a drag
            // inside what is legal, so this is the splice refusing for its own reasons — and a list
            // showing an order the drawing does not have would be worse than the drag not landing.
            if (!_dropped(dragged, to))
            {
                _rows.Move(to, _from);
            }
        }

        private Control? Row(int index)
            => index >= 0 && index < _rows.Count ? _items.ContainerFromIndex(index) as Control : null;
    }

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
        var open = _hasDocument && _canDeclare;

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
