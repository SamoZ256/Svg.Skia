---
title: "Embedding SvgEditorWorkspace"
---

# Embedding SvgEditorWorkspace

`SvgEditorWorkspace` is the fastest way to ship the editor in an Avalonia application. It already composes the menu bar, tool palette, document outline, resource browser, canvas, property inspector, and status bar.

## Install

```bash
dotnet add package SamoZ256.Svg.Editor.Skia.Avalonia --prerelease
```

## Minimal host window

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:editor="clr-namespace:Svg.Editor.Skia.Avalonia;assembly=Svg.Editor.Skia.Avalonia"
        x:Class="MyApp.MainWindow"
        Width="1200"
        Height="800">
  <editor:SvgEditorWorkspace x:Name="EditorWorkspace" />
</Window>
```

```csharp
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg;
using Svg.Editor.Avalonia;
using Svg.Editor.Skia.Avalonia;

namespace MyApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        EditorWorkspace.WorkspaceTitlePrefix = "MyApp";
        EditorWorkspace.ResourceAssembly = typeof(MainWindow).Assembly;
        EditorWorkspace.DialogService = new SvgEditorDialogService();
        EditorWorkspace.FileDialogService = new SvgEditorFileDialogService();
        EditorWorkspace.PreviewRequested = ShowPreviewAsync;
        EditorWorkspace.WorkspaceTitleChanged += (_, title) => Title = title;

        EditorWorkspace.LoadDocument("Assets/__tiger.svg");
        Title = EditorWorkspace.WorkspaceTitle;
    }

    private Task ShowPreviewAsync(SvgDocument document)
    {
        var preview = new Window
        {
            Title = "Preview"
        };

        return preview.ShowDialog(this);
    }
}
```

`LoadDocument(...)` accepts either a file-system path or an `avares`-resolvable asset path. Set `ResourceAssembly` when the document lives in the host application's resources instead of the editor package assembly.

## Host-controlled commands

The built-in menu uses the same public methods that your own shell can call:

- `LoadDocument(string path)`
- `OpenDocumentAsync()`
- `SaveDocumentAsync()`
- `ExportSelectedElementAsync()`
- `ExportPdfAsync()`
- `ExportXpsAsync()`
- `PlaceImageAsync()`
- `ShowSettingsAsync()`

That means you can keep the workspace layout but move the commands to your own menu, toolbar, or command system.

## Important public properties

| Member | Purpose |
| --- | --- |
| `Session` | Access the shared `SvgEditorSession` for document, title, selection ids, settings, nodes, artboards, and history |
| `Surface` | Access the `SvgEditorSurface` control directly |
| `WorkspaceTitlePrefix` | Replace the default `SVG Editor` branding |
| `ResourceAssembly` | Resolve embedded `avares` document paths against the host assembly |
| `DialogService` | Replace modal editor presentation |
| `FileDialogService` | Replace open/save/export/image pickers |
| `PreviewRequested` | Hook preview behavior without coupling the package to a window type |

## When to drop down a level

Use the full workspace when the default three-column editor layout matches your app.

Drop down to the lower packages when:

- the host already has its own document browser or shell chrome,
- file picking must go through a custom storage abstraction,
- dialogs need to be inline flyouts or docked panes rather than windows,
- you want a custom surface layout around `SvgEditorSurface`.

For that composition path, continue with [Composing Panels and Dialogs](composing-panels-and-dialogs).
