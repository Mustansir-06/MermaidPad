// MIT License
// Copyright (c) 2025 Dave Black
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;
using MermaidPad.ViewModels;
using Microsoft.Extensions.Logging;

namespace MermaidPad.Infrastructure;

/// <summary>
/// Factory for creating and configuring the docking layout for MermaidPad.
/// </summary>
public sealed class DockFactory : Factory
{
    private readonly MainViewModel _mainViewModel;
    private readonly ILogger<DockFactory>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DockFactory"/> class.
    /// </summary>
    /// <param name="mainViewModel">The main view model containing the panel view models.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public DockFactory(MainViewModel mainViewModel, ILogger<DockFactory>? logger = null)
    {
        _mainViewModel = mainViewModel;
        _logger = logger;
    }

    /// <summary>
    /// Creates and configures the root dock layout for the application, including editor, preview, and AI assistant
    /// panels arranged horizontally.
    /// </summary>
    /// <remarks>The returned layout includes three tool docks—Editor, Preview, and AI Assistant—organized in
    /// a horizontal split. Each tool dock is initialized with its respective context to support data binding in user
    /// controls. The layout is suitable for use as the application's primary docking structure.</remarks>
    /// <returns>An <see cref="IRootDock"/> instance representing the main layout with editor, preview, and AI assistant tool
    /// docks.</returns>
    public override IRootDock CreateLayout()
    {
        // Create tool docks for each panel
        // Context is set to MainViewModel so DataContext binding works in UserControls
        Tool editorTool = new Tool
        {
            Id = "Editor",
            Title = "Editor",
            CanClose = false,
            CanFloat = true,
            CanPin = true,
            Context = _mainViewModel
        };

        Tool previewTool = new Tool
        {
            Id = "Preview",
            Title = "Preview",
            CanClose = false,
            CanFloat = true,
            CanPin = true,
            Context = _mainViewModel
        };

        Tool aiTool = new Tool
        {
            Id = "AIAssistant",
            Title = "AI Assistant",
            CanClose = false,
            CanFloat = true,
            CanPin = true,
            Context = _mainViewModel.AIPanelViewModel
        };

        // Create proportional dock splitter for horizontal layout (Editor | Preview | AI)
        ProportionalDock proportionalDock = new ProportionalDock
        {
            Id = "MainProportionalDock",
            Title = "MainProportionalDock",
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>
            (
                new ToolDock
                {
                    Id = "EditorDock",
                    Title = "EditorDock",
                    Proportion = 0.33, // 33% width
                    VisibleDockables = CreateList<IDockable>(editorTool),
                    ActiveDockable = editorTool
                },
                new ProportionalDockSplitter
                {
                    Id = "Splitter1",
                    Title = "Splitter1"
                },
                new ToolDock
                {
                    Id = "PreviewDock",
                    Title = "PreviewDock",
                    Proportion = 0.34, // 34% width
                    VisibleDockables = CreateList<IDockable>(previewTool),
                    ActiveDockable = previewTool
                },
                new ProportionalDockSplitter
                {
                    Id = "Splitter2",
                    Title = "Splitter2"
                },
                new ToolDock
                {
                    Id = "AIDock",
                    Title = "AIDock",
                    Proportion = 0.33, // 33% width
                    VisibleDockables = CreateList<IDockable>(aiTool),
                    ActiveDockable = aiTool
                }
            )
        };

        // Create root dock
        RootDock rootDock = new RootDock
        {
            Id = "Root",
            Title = "Root",
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(proportionalDock),
            ActiveDockable = proportionalDock,
            DefaultDockable = proportionalDock
        };

        return rootDock;
    }

    /// <summary>
    /// Initializes the docking layout.
    /// </summary>
    /// <param name="layout">The root dock layout to initialize.</param>
    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Editor"] = () => _mainViewModel,
            ["Preview"] = () => _mainViewModel,
            ["AIAssistant"] = () => _mainViewModel.AIPanelViewModel
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = static () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    /// <summary>
    /// Serializes the current dock layout to a JSON string.
    /// </summary>
    /// <param name="layout">The root dock layout to serialize.</param>
    /// <returns>A JSON string representing the dock layout, or null if serialization fails.</returns>
    public string? SerializeLayout(IDock layout)
    {
        try
        {
            DockSerializer serializer = new DockSerializer(typeof(ObservableCollection<>));
            string json = serializer.Serialize(layout);
            _logger?.LogInformation("Dock layout serialized successfully");
            return json;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to serialize dock layout");
            return null;
        }
    }

    /// <summary>
    /// Deserializes a dock layout from a JSON string and restores contexts.
    /// </summary>
    /// <param name="json">The JSON string representing the dock layout.</param>
    /// <returns>The deserialized root dock, or null if deserialization fails.</returns>
    public IDock? DeserializeLayout(string json)
    {
        try
        {
            DockSerializer serializer = new DockSerializer(typeof(ObservableCollection<>));
            IDock? layout = serializer.Deserialize<IDock>(json);

            if (layout is not null)
            {
                // Restore contexts for all tools after deserialization
                RestoreContexts(layout);
                _logger?.LogInformation("Dock layout deserialized successfully");
            }

            return layout;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to deserialize dock layout");
            return null;
        }
    }

    /// <summary>
    /// Recursively restores the context for all dockable items in the layout.
    /// </summary>
    /// <param name="dockable">The dockable item to process.</param>
    private void RestoreContexts(IDockable dockable)
    {
        // Restore context for Tool items based on their ID
        if (dockable is Tool tool && tool.Id is not null)
        {
            tool.Context = tool.Id switch
            {
                "Editor" => _mainViewModel,
                "Preview" => _mainViewModel,
                "AIAssistant" => _mainViewModel.AIPanelViewModel,
                _ => tool.Context
            };
        }

        // Recursively process child dockables
        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (IDockable? child in dock.VisibleDockables)
            {
                if (child is not null)
                {
                    RestoreContexts(child);
                }
            }
        }
    }
}
