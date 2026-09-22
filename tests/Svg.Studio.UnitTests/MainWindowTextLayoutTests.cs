using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Svg;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Exporting a project whose words an expression writes: what is asked, and what comes out.
/// </summary>
/// <remarks>
/// Driven through <see cref="MainWindow.ExportAsync(string)"/> and the replaceable dialog, for the
/// reason every other modal here is: a panel nobody clicks blocks a headless run for ever.
/// </remarks>
[Collection("settings")]
public class MainWindowTextLayoutTests : IDisposable
{
    /// <summary>A drawing laid out the one way that can carry a parameter: one run, one origin.</summary>
    private const string Carried = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 64 24" width="64" height="24">
          <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
          <text x="32" y="18" text-anchor="middle" font-family="Arial" font-size="12" fill="#111827">{{ step }}</text>
        </svg>
        """;

    /// <summary>The same words placed by a span of their own, which is measured before anything is drawn.</summary>
    private const string Frozen = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 64 24" width="64" height="24">
          <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
          <text x="32" y="18" font-family="Arial" font-size="12" fill="#111827"><tspan x="4" y="18">{{ step }}</tspan></text>
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    private readonly string _store = StudioSettings.Store;

    public MainWindowTextLayoutTests()
        => StudioSettings.Store = Path.Combine(Directory.CreateTempSubdirectory().FullName, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _store;
        Directory.Delete(_directory, recursive: true);
    }

    private string Project(string drawing)
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, $"""
            <studio namespace="Demo.Icons">
              <drawing name="badge" class="Badge">
            {drawing}
              </drawing>
            </studio>
            """);

        return path;
    }

    private async Task<MainWindow> Host(string drawing)
    {
        var window = new MainWindow();

        window.Announce = (_, _) => Task.CompletedTask;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { Project(drawing) });
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private string Target => Path.Combine(_directory, "Icons.cs");

    /// <summary>
    /// A project the export can write out in full is never asked about. This is most projects: an
    /// icon set without a word in it drives nothing, and a set that does may still be laid out the
    /// way a command can carry.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_Is_Asked_When_Nothing_Would_Be_Lost()
    {
        var window = await Host(Carried);
        var asked = false;

        window.ConfirmRelax = _ =>
        {
            asked = true;

            return Task.FromResult<SvgTextLayout?>(SvgTextLayout.Baked);
        };

        Assert.True(await window.ExportAsync(Target));

        Assert.False(asked);
        Assert.Contains("DrawText(step__default, ", File.ReadAllText(Target), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task What_Would_Be_Lost_Is_Named_Before_Anything_Is_Written()
    {
        var window = await Host(Frozen);
        var said = new List<string>();

        window.ConfirmRelax = frozen =>
        {
            said.AddRange(frozen);

            return Task.FromResult<SvgTextLayout?>(SvgTextLayout.Baked);
        };

        Assert.True(await window.ExportAsync(Target));

        // By drawing and by element, since that is what somebody has to go and look at.
        Assert.Contains(said, line => line.Contains("badge", StringComparison.Ordinal) && line.Contains("<tspan>", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Relaxing_Draws_The_Argument_And_Keeping_The_Default_Writes_It_In()
    {
        var window = await Host(Frozen);

        window.ConfirmRelax = _ => Task.FromResult<SvgTextLayout?>(SvgTextLayout.Relaxed);

        Assert.True(await window.ExportAsync(Target));
        Assert.Contains("DrawText(step__default, ", File.ReadAllText(Target), StringComparison.Ordinal);

        window.ConfirmRelax = _ => Task.FromResult<SvgTextLayout?>(SvgTextLayout.Baked);

        Assert.True(await window.ExportAsync(Target));

        var baked = File.ReadAllText(Target);

        Assert.Contains("DrawText(\"1\", ", baked, StringComparison.Ordinal);
        Assert.DoesNotContain("string step", baked, StringComparison.Ordinal);
    }

    /// <summary>Cancelling the question cancels the export: the file it would write is not written.</summary>
    [AvaloniaFact]
    public async Task An_Unanswered_Question_Writes_Nothing()
    {
        var window = await Host(Frozen);

        window.ConfirmRelax = _ => Task.FromResult<SvgTextLayout?>(null);

        Assert.False(await window.ExportAsync(Target));
        Assert.False(File.Exists(Target));
    }

    [AvaloniaFact]
    public async Task An_Answer_That_Is_Remembered_Is_Not_Asked_Again()
    {
        var window = await Host(Frozen);
        var asked = 0;

        window.ConfirmRelax = _ =>
        {
            asked++;

            // What the dialog's own box does when it is ticked.
            StudioSettings.RelaxedText = RelaxedTextAnswer.Always;

            return Task.FromResult<SvgTextLayout?>(SvgTextLayout.Relaxed);
        };

        Assert.True(await window.ExportAsync(Target));
        Assert.True(await window.ExportAsync(Target));

        Assert.Equal(1, asked);
        Assert.Contains("DrawText(step__default, ", File.ReadAllText(Target), StringComparison.Ordinal);
    }

    /// <summary>The settings window shows the answer on file, and writes the one that is picked.</summary>
    [AvaloniaFact]
    public void The_Setting_Is_Shown_And_Written_Where_It_Is_Set()
    {
        StudioSettings.RelaxedText = RelaxedTextAnswer.Never;

        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        // "Keep the default text", which is what Never is called where somebody reads it.
        Assert.Equal(2, settings.RelaxedText.SelectedIndex);

        settings.RelaxedText.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RelaxedTextAnswer.Always, StudioSettings.RelaxedText);

        settings.Close();
    }

    /// <summary>
    /// The setting answers for every export, including the first: a project that would have been
    /// asked about is built the way the answer says without the survey ever running.
    /// </summary>
    [AvaloniaFact]
    public async Task The_Setting_Answers_Without_Asking()
    {
        StudioSettings.RelaxedText = RelaxedTextAnswer.Never;

        var window = await Host(Frozen);

        window.ConfirmRelax = _ => throw new InvalidOperationException("Asked although the setting had already answered.");

        Assert.True(await window.ExportAsync(Target));
        Assert.Contains("DrawText(\"1\", ", File.ReadAllText(Target), StringComparison.Ordinal);
    }
}
