// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// One row of the variables a document declares, whatever kind of variable it is.
/// </summary>
/// <remarks>
/// What a parameter row and an expression row have in common, which is everything about being a row
/// and nothing about being a value: where it came from, whether it wears the heading saying so, and
/// how it tells a control that something changed. Both halves of this were written out twice, word
/// for word, before the pane showed them in one list.
///
/// Not <c>Name</c>, and not <c>IsModified</c>. A parameter's name is its declaration's and cannot be
/// typed over; an expression row's name is a draft somebody is editing. And the two mean different
/// things by modified — a parameter differs from its declared default, while an expression row
/// differs from what the document says — so one name over both would quietly change what the commit
/// affordance means.
/// </remarks>
public abstract class SvgViewerVariable : INotifyPropertyChanged
{
    private bool _showsOwner;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where this was declared, where that is worth saying.</summary>
    /// <remarks>
    /// The name of whatever declares it, not a sentence about it: the panel says it once over the
    /// run rather than on every row. Null for a panel whose host does not answer, which is a panel
    /// whose rows all came from the one document.
    /// </remarks>
    public string? OwnerLabel { get; set; }

    /// <summary>Whether this row begins a run declared somewhere new, and so wears the heading.</summary>
    /// <remarks>
    /// Set by the panel, which is the only thing that can see a row's neighbours. Raises a change
    /// because the rows outlive a refresh: the same row can begin a run one moment and sit inside
    /// one the next, when the section above it appears.
    /// </remarks>
    public bool ShowsOwner
    {
        get => _showsOwner;
        set
        {
            if (_showsOwner == value)
            {
                return;
            }

            _showsOwner = value;
            Raise(nameof(ShowsOwner));
        }
    }

    protected void Raise(string? property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(property);
        Changed();
    }

    /// <summary>What else this row re-raises when any of its values changes.</summary>
    /// <remarks>
    /// The derived things a row shows beside the value it holds — whether it differs from the
    /// document, and what it currently comes to. Which those are is the one part of a change that
    /// differs between the two kinds, so it is the one part left to them.
    /// </remarks>
    protected virtual void Changed()
    {
    }
}
