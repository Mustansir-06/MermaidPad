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

using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;

namespace MermaidPad.Views.Panels;

/// <summary>
/// UserControl wrapper for the Editor panel that exposes the TextEditor control.
/// </summary>
public partial class EditorPanel : UserControl
{
    /// <summary>
    /// Gets the TextEditor control contained in this panel.
    /// </summary>
    public TextEditor Editor { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EditorPanel"/> class.
    /// </summary>
    public EditorPanel()
    {
        InitializeComponent();
        Editor = this.FindControl<TextEditor>("Editor")
            ?? throw new InvalidOperationException("Editor control not found in EditorPanel");
    }

    /// <summary>
    /// Handles the context menu opening event for the Editor.
    /// </summary>
    /// <param name="sender">The context menu.</param>
    /// <param name="e">Event arguments.</param>
    private void GetContextMenuState(object? sender, RoutedEventArgs e)
    {
        // This will be called from XAML - the actual logic is in MainWindow
        // We need to forward this event or the DataContext will handle it
    }
}
