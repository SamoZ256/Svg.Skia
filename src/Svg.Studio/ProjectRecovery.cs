// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;

namespace Svg.Studio;

/// <summary>
/// A copy of the project as it stands, kept where a crash cannot take it.
/// </summary>
/// <remarks>
/// <para>
/// Never the project's own file: the whole of this change is that the file is the author's to write.
/// What is kept is a copy under application data, offered back when the project is opened again and
/// thrown away the moment the project is saved — so a copy exists exactly when Studio did not leave
/// cleanly with that project open.
/// </para>
/// <para>
/// The copy is a project file, byte for byte what a save would write. Nothing is stored inside it —
/// a <c>&lt;studio&gt;</c> refuses attributes it does not know, and a wrapper element would throw
/// away the byte-exact round trip the format is held in. The project's own path is the key instead,
/// so the copy is found by computing a name rather than by reading an index, and a project that has
/// never been written is covered like any other.
/// </para>
/// <para>
/// What it holds is the project. Text typed into a drawing's editor is that tab's buffer and does
/// not reach the document until the tab is saved; pulling every open buffer in on a timer would mark
/// tabs and rebuild drawings as a side effect of a clock.
/// </para>
/// </remarks>
public sealed class ProjectRecovery
{
    /// <summary>How long a copy is kept for a project nobody came back to.</summary>
    private const int Days = 14;

    /// <summary>How long one edit is left unwritten, and the least any number of them waits.</summary>
    /// <remarks>
    /// Half a minute is a typed setting's worth of work, which is what a single edit costs to lose.
    /// The floor is a guess and wants measuring: writing the copy serialises the whole document on
    /// the UI thread, and an imported PaintCode document runs to 16 MB and a thousand drawings.
    /// </remarks>
    private const double Patience = 30d;
    private const double Floor = 3d;

    private readonly ProjectWorkspace _workspace;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1d) };

    /// <summary>The edit count the last copy was written at, so the rest is what a crash would cost.</summary>
    private int _written;
    private DateTime _since;
    private int _failures;

    public ProjectRecovery(ProjectWorkspace workspace, string path)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Path = For(path ?? throw new ArgumentNullException(nameof(path)));

        _clock.Tick += (_, _) => Pulse();

        _workspace.Edited += OnEdited;
        _workspace.Saved += OnSaved;
    }

    /// <summary>
    /// Where the copies are kept.
    /// </summary>
    /// <remarks>Settable so a test drives a directory of its own instead of the one on this machine.</remarks>
    public static string Store { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Svg.Studio",
        "recovery");

    /// <summary>
    /// What the clock reads.
    /// </summary>
    /// <remarks>Settable so a test drives one tick instead of waiting for one.</remarks>
    public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>The copy this project's work goes to.</summary>
    public string Path { get; }

    /// <summary>How a failure that has stopped being a fluke is reported.</summary>
    /// <remarks>
    /// Given by the host, because saying anything is a window's job. Silence is right for one failed
    /// copy and wrong for a feature that has stopped working: what this protects is somebody's
    /// belief that their work is covered, and that belief is the whole of it.
    /// </remarks>
    public Action<string, string>? Trouble { get; set; }

    /// <summary>Whether the clock is running, which it does only while there is something to write.</summary>
    public bool IsArmed => _clock.IsEnabled;

    /// <summary>The copy kept for a project, whether or not one is there.</summary>
    public static string For(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).Substring(0, 16).ToLowerInvariant();

        // Hashed rather than hashed by the runtime: String.GetHashCode is salted per process, so
        // every copy would be looked for under a name nothing had written.
        return System.IO.Path.Combine(Store, $"{System.IO.Path.GetFileNameWithoutExtension(full)}-{key}.svgstudio");
    }

    /// <summary>The copy waiting for a project, or null where none is or it says the same thing.</summary>
    /// <remarks>
    /// Compared rather than dated. A project written by something else after a crash is newer than
    /// the copy and still has never seen the work in it, and a copy that outlived a save whose
    /// delete failed is identical and worth nobody's question — so the text answers both, and the
    /// dates are left to say when.
    /// </remarks>
    public static string? Waiting(string path, string text)
    {
        var recovery = For(path);

        if (Read(recovery) is not { } kept)
        {
            return null;
        }

        if (string.Equals(kept, text, StringComparison.Ordinal))
        {
            Delete(recovery);

            return null;
        }

        return recovery;
    }

    /// <summary>A copy as text, or null where there is none to read.</summary>
    public static string? Read(string recovery)
    {
        try
        {
            // Decoded rather than read as text, which strips a byte order mark: the mark is a
            // character of the project, the same as it is when one is opened.
            return File.Exists(recovery)
                ? new UTF8Encoding(false).GetString(File.ReadAllBytes(recovery))
                : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string recovery)
    {
        try
        {
            File.Delete(recovery);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Throws away every copy, for somebody who has just said they do not want them.</summary>
    public static void Clear()
    {
        foreach (var file in Kept())
        {
            Delete(file);
        }
    }

    /// <summary>Drops copies nobody came back for, so the directory does not grow for ever.</summary>
    /// <remarks>
    /// By age, which is the only question that can be asked of them: the name is a one-way hash, so
    /// a copy cannot say which project it belongs to or whether that project still exists.
    /// </remarks>
    public static void Sweep()
    {
        foreach (var file in Kept())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-Days))
                {
                    Delete(file);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>How long work of this size is left unwritten.</summary>
    /// <remarks>The more there is to lose, the sooner it is worth the write.</remarks>
    public static TimeSpan Cooldown(int edits)
        => TimeSpan.FromSeconds(edits <= 0 ? Patience : Math.Max(Floor, Patience / edits));

    /// <summary>One turn of the clock: writes the copy if enough has built up behind it.</summary>
    /// <remarks>
    /// Public, and separate from the timer that calls it, so a test drives a tick rather than
    /// waiting half a minute for one.
    /// </remarks>
    /// <returns>Whether a copy was written.</returns>
    public bool Pulse()
    {
        var pressure = Pressure;

        if (pressure == 0 || !StudioSettings.Autosave)
        {
            _clock.Stop();

            return false;
        }

        if (Now() - _since < Cooldown(pressure))
        {
            return false;
        }

        return Write();
    }

    /// <summary>Throws away the copy, for work that is now somewhere better.</summary>
    public void Drop()
    {
        _clock.Stop();
        _written = _workspace.Edits;

        Delete(Path);
    }

    /// <summary>Stops watching the project, which is closing or already closed.</summary>
    public void Stop()
    {
        _clock.Stop();

        _workspace.Edited -= OnEdited;
        _workspace.Saved -= OnSaved;
    }

    private int Pressure => Math.Max(0, _workspace.Edits - _written);

    private static string[] Kept()
    {
        try
        {
            return Directory.Exists(Store) ? Directory.GetFiles(Store) : Array.Empty<string>();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void OnEdited(object? sender, EventArgs e)
    {
        if (_clock.IsEnabled || Pressure == 0 || !StudioSettings.Autosave)
        {
            return;
        }

        // Timed from the first edit of this run rather than from each one: a clock restarted by
        // every edit is never reached by a drag, which raises them continuously and is the gesture
        // most worth covering.
        _since = Now();

        _clock.Start();
    }

    private void OnSaved(object? sender, EventArgs e) => Drop();

    private bool Write()
    {
        var at = _workspace.Edits;
        var temporary = Path + ".tmp";

        try
        {
            Directory.CreateDirectory(Store);

            // Written beside and moved over: a copy caught half written is the one thing worse than
            // no copy, since it would be offered back as if it were the work.
            File.WriteAllText(temporary, _workspace.Document.ToXml(), new UTF8Encoding(false));
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Quietly, and tried again a whole cooldown later rather than every second — while the
            // work piles up behind it, which shortens that cooldown by itself.
            _since = Now();
            _failures++;

            if (_failures == 3)
            {
                Trouble?.Invoke("Work is not being copied", $"{failure.Message} Save the project to be sure of it.");
            }

            return false;
        }

        _written = at;
        _failures = 0;

        _clock.Stop();

        return true;
    }
}
