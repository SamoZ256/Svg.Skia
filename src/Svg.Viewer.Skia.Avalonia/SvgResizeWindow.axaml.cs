// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Asks what size a drawing should be, over <see cref="SvgViewerResize"/>.</summary>
/// <remarks>
/// The window holds no arithmetic of its own: every box writes into the resize and every box is
/// written back from it, so the three of them cannot drift apart. What that means on screen is that
/// typing a width moves the height and the scale while the ratio is locked, and moves nothing else
/// once it is not.
/// </remarks>
public partial class SvgResizeWindow : Window
{
    private readonly SvgViewerResize _resize;

    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly TextBox _scale;
    private readonly CheckBox _lock;
    private readonly TextBlock _note;

    /// <summary>Whether a box is being filled in from the model rather than by a person.</summary>
    /// <remarks>
    /// Writing a box raises its own TextChanged, which would read the half-formatted value back into
    /// the model and round it while somebody is still typing.
    /// </remarks>
    private bool _writing;

    public SvgResizeWindow()
        : this(new SvgViewerResize(100f, 100f))
    {
    }

    public SvgResizeWindow(SvgViewerResize resize)
    {
        _resize = resize ?? throw new ArgumentNullException(nameof(resize));

        InitializeComponent();

        _width = this.FindControl<TextBox>("WidthBox")!;
        _height = this.FindControl<TextBox>("HeightBox")!;
        _scale = this.FindControl<TextBox>("ScaleBox")!;
        _lock = this.FindControl<CheckBox>("LockBox")!;
        _note = this.FindControl<TextBlock>("NoteText")!;

        _lock.IsChecked = resize.IsAspectRatioLocked;

        Show(_resize);

        _width.TextChanged += (_, _) => Read(_width, value => _resize.SetWidth(value));
        _height.TextChanged += (_, _) => Read(_height, value => _resize.SetHeight(value));
        _scale.TextChanged += (_, _) => Read(_scale, value => _resize.SetScale(value));

        _lock.IsCheckedChanged += (_, _) =>
        {
            _resize.IsAspectRatioLocked = _lock.IsChecked == true;
            Show(_resize);
        };

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("ResizeButton")!.Click += (_, _) => Close(_resize.ToRequest());
    }

    /// <summary>Takes one box's text into the model, and the model back into the others.</summary>
    private void Read(TextBox box, Action<float> set)
    {
        if (_writing)
        {
            return;
        }

        // A box mid-edit is not a refusal: somebody clearing it to type a new number has written
        // nothing yet, and the note is for what they finish saying.
        if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        try
        {
            set(value);
            Note(null);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            Note(failure.Message);

            return;
        }

        Show(_resize, except: box);
    }

    /// <summary>Writes the model into the boxes, leaving the one being typed in alone.</summary>
    private void Show(SvgViewerResize resize, TextBox? except = null)
    {
        _writing = true;

        try
        {
            if (!ReferenceEquals(except, _width))
            {
                _width.Text = Text(resize.Width);
            }

            if (!ReferenceEquals(except, _height))
            {
                _height.Text = Text(resize.Height);
            }

            // A scale says nothing without the lock, so it is emptied rather than left showing a
            // factor that no longer describes both axes.
            _scale.IsEnabled = resize.IsAspectRatioLocked;

            if (!ReferenceEquals(except, _scale))
            {
                _scale.Text = resize.IsAspectRatioLocked ? Text(resize.Scale) : string.Empty;
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private void Note(string? message)
    {
        _note.Text = message;
        _note.IsVisible = message is { };
    }

    private static string Text(float value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
