// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// One let a document declares, as something a row of boxes can be bound to.
/// </summary>
/// <remarks>
/// The name and the body are drafts: they follow what is typed, while <see cref="Declaration"/>
/// stays at what the document says until an edit is committed. Nothing here reaches the drawing —
/// a let with a half-typed body would stop it rendering.
/// </remarks>
public sealed class SvgViewerLet : INotifyPropertyChanged
{
    private string _name;
    private string _expression;
    private string _readout = string.Empty;
    private string? _trouble;

    /// <param name="declaration">What the document says, or null for a row nobody has committed yet.</param>
    public SvgViewerLet(SvgExpressionLet? declaration)
    {
        Declaration = declaration;
        _name = declaration?.Name ?? string.Empty;
        _expression = declaration?.Expression ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SvgExpressionLet? Declaration { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    public string Expression
    {
        get => _expression;
        set => Set(ref _expression, value ?? string.Empty);
    }

    /// <summary>What the let currently evaluates to, or empty where that cannot be said.</summary>
    /// <remarks>
    /// Empty while the row differs from the document, rather than cleared on every keystroke: the
    /// value belongs to the declared body, so it goes as soon as what is typed is not that body.
    /// </remarks>
    public string Readout
    {
        get => IsModified ? string.Empty : _readout;
        set => Set(ref _readout, value ?? string.Empty);
    }

    /// <summary>What is wrong with the body as typed, or null.</summary>
    public string? Trouble
    {
        get => _trouble;
        set
        {
            Set(ref _trouble, value);
            Raise(nameof(HasTrouble));
        }
    }

    public bool HasTrouble => _trouble is { };

    /// <summary>Whether what is typed differs from what the document declares.</summary>
    public bool IsModified
        => Declaration is not { } declared
           || !string.Equals(_name, declared.Name, StringComparison.Ordinal)
           || !string.Equals(_expression, declared.Expression, StringComparison.Ordinal);

    /// <summary>Whether this is the empty row somebody is filling in rather than a declared let.</summary>
    public bool IsDraft => Declaration is null;

    public void Revert()
    {
        Name = Declaration?.Name ?? string.Empty;
        Expression = Declaration?.Expression ?? string.Empty;
        Trouble = null;
    }

    private void Raise(string? property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(property);
        Raise(nameof(IsModified));
        Raise(nameof(Readout));
    }
}
