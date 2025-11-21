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
/// <remarks>
/// This factory follows the ContextLocator pattern from the Dock library documentation.
/// ViewModels are not stored in the factory; instead, they are passed as parameters to
/// initialization methods where ContextLocator closures capture them.
/// </remarks>
public sealed class DockFactory : Factory
{
    private readonly DockSerializer _serializer;
    private readonly ILogger<DockFactory>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DockFactory"/> class.
    /// </summary>
    /// <param name="serializer">The dock serializer for saving/loading layouts.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public DockFactory(DockSerializer serializer, ILogger<DockFactory>? logger = null)
    {
        _serializer = serializer;
        _logger = logger;
    }

    /// <summary>
    /// Creates and configures the root dock layout for the application, including editor, preview, and AI assistant
    /// panels arranged horizontally.
    /// </summary>
    /// <remarks>
    /// The returned layout includes three tool docks—Editor, Preview, and AI Assistant—organized in
    /// a horizontal split. Contexts are set via ContextLocator in InitLayout, following the recommended
    /// pattern from the Dock library documentation.
    /// </remarks>
    /// <returns>An <see cref="IRootDock"/> instance representing the main layout with editor, preview, and AI assistant tool
    /// docks.</returns>
    public override IRootDock CreateLayout()
    {
        // Create tool docks for each panel
        // Note: Contexts will be assigned via ContextLocator in InitLayout()
        Tool editorTool = new Tool
        {
            Id = "Editor",
            Title = "Editor",
            CanClose = false,
            CanFloat = true,
            CanPin = true
        };

        Tool previewTool = new Tool
        {
            Id = "Preview",
            Title = "Preview",
            CanClose = false,
            CanFloat = true,
            CanPin = true
        };

        Tool aiTool = new Tool
        {
            Id = "AIAssistant",
            Title = "AI Assistant",
            CanClose = false,
            CanFloat = true,
            CanPin = true
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
    /// Initializes the docking layout with ViewModel context mappings.
    /// </summary>
    /// <param name="layout">The root dock layout to initialize.</param>
    /// <param name="viewModel">The main view model containing panel view models for context binding.</param>
    /// <remarks>
    /// This follows the ContextLocator pattern from the Dock library documentation.
    /// ViewModel references are captured by closures in the ContextLocator dictionary,
    /// avoiding the need to store ViewModels as fields in the factory.
    /// </remarks>
    public void InitLayout(IDockable layout, MainViewModel viewModel)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Editor"] = () => viewModel,
            ["Preview"] = () => viewModel,
            ["AIAssistant"] = () => viewModel.AIPanelViewModel
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
            string json = _serializer.Serialize(layout);
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
    /// <param name="viewModel">The main view model containing panel view models for context restoration.</param>
    /// <returns>The deserialized root dock, or null if deserialization fails.</returns>
    /// <remarks>
    /// After deserialization, this method restores ViewModel contexts for all tools based on their ID.
    /// This is necessary because contexts (ViewModel references) are not serialized.
    /// </remarks>
    public IDock? DeserializeLayout(string json, MainViewModel viewModel)
    {
        try
        {
            IDock? layout = _serializer.Deserialize<IDock>(json);

            if (layout is not null)
            {
                // Restore contexts for all tools after deserialization
                RestoreContexts(layout, viewModel);
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
    /// <param name="viewModel">The main view model containing panel view models.</param>
    private void RestoreContexts(IDockable dockable, MainViewModel viewModel)
    {
        // Restore context for Tool items based on their ID
        if (dockable is Tool tool && tool.Id is not null)
        {
            tool.Context = tool.Id switch
            {
                "Editor" => viewModel,
                "Preview" => viewModel,
                "AIAssistant" => viewModel.AIPanelViewModel,
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
                    RestoreContexts(child, viewModel);
                }
            }
        }
    }
}
