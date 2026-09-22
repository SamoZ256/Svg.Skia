namespace Svg.Studio.UnitTests;

/// <summary>One gesture on a project, standing in for whatever would have raised it.</summary>
/// <remarks>
/// The tests about counting edits — the recovery clock's pressure, the unsaved mark, the tab marker
/// — care that a gesture happened and not what it was. The window used to offer a bare <c>Edit()</c>
/// they could call; it does not any more, because an edit that cannot be taken back is exactly what
/// the project's history exists to stop anybody writing. This goes through <c>Do</c> like every real
/// one, so what those tests raise is the same shape of thing the editor raises.
/// </remarks>
internal static class Edits
{
    internal static void Edit(this ProjectWorkspace workspace, string label = "edit")
        => workspace.Do(label, () => ProjectSnapshot.Attributes(workspace.Document.Root), () => { });
}
