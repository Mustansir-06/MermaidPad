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

using AsyncAwaitBestPractices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaWebView;
using Dock.Avalonia.Controls;
using MermaidPad.Exceptions.Assets;
using MermaidPad.Extensions;
using MermaidPad.Services;
using MermaidPad.Services.Highlighting;
using MermaidPad.ViewModels;
using MermaidPad.Views.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MermaidPad.Views;

/// <summary>
/// Main application window that contains the editor and preview WebView.
/// Manages synchronization between the editor control and the <see cref="MainViewModel"/>,
/// initializes and manages the <see cref="MermaidRenderer"/>, and handles window lifecycle events.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MermaidRenderer _renderer;
    private readonly MermaidUpdateService _updateService;
    private readonly SyntaxHighlightingService _syntaxHighlightingService;
    private readonly IDebounceDispatcher _editorDebouncer;
    private readonly ILogger<MainWindow> _logger;

    private bool _isClosingApproved;
    private bool _suppressEditorTextChanged;
    private bool _suppressEditorStateSync; // Prevent circular updates

    private const int WebViewReadyTimeoutSeconds = 30;

    // Controls accessed from Dock panels
    private TextEditor? _editor;
    private WebView? _preview;

    // Properties to access controls (for code that expects them as properties)
    private TextEditor Editor => _editor ?? throw new InvalidOperationException("Editor not initialized yet");
    private WebView Preview => _preview ?? throw new InvalidOperationException("Preview not initialized yet");

    // Event handlers stored for proper cleanup
    private EventHandler? _editorTextChangedHandler;
    private EventHandler? _editorSelectionChangedHandler;
    private EventHandler? _editorCaretPositionChangedHandler;
    private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: Constructor
    /// - InitializeComponent() MUST be first
    /// - Resolve services from DI
    /// - Set DataContext
    /// - Initialize simple fields only
    ///
    /// DO NOT wire event handlers here - use OnAttachedToVisualTree instead.
    /// </remarks>
    public MainWindow()
    {
        InitializeComponent();

        IServiceProvider sp = App.Services;
        _editorDebouncer = sp.GetRequiredService<IDebounceDispatcher>();
        _renderer = sp.GetRequiredService<MermaidRenderer>();
        _vm = sp.GetRequiredService<MainViewModel>();
        _updateService = sp.GetRequiredService<MermaidUpdateService>();
        _syntaxHighlightingService = sp.GetRequiredService<SyntaxHighlightingService>();
        _logger = sp.GetRequiredService<ILogger<MainWindow>>();
        DataContext = _vm;

        _logger.LogInformation("MainWindow constructor completed - event handlers will be wired in OnAttachedToVisualTree");
    }

    /// <summary>
    /// Attempts to find EditorPanel and PreviewPanel from DockControl DataTemplates.
    /// </summary>
    /// <remarks>
    /// DataTemplates in DockControl are applied lazily. This method searches the visual tree
    /// for the panels and wires up their event handlers.
    ///
    /// If panels are not found, retries after a short delay (DataTemplates may still be applying).
    /// </remarks>
    private void TryFindDockPanels()
    {
        DockControl? dockControl = this.FindControl<DockControl>("MainDock");
        if (dockControl is null)
        {
            _logger.LogError("DockControl 'MainDock' not found - cannot initialize panels");
            return;
        }

        // Find EditorPanel from DataTemplate
        EditorPanel? editorPanel = dockControl.GetVisualDescendants().OfType<EditorPanel>().FirstOrDefault();
        PreviewPanel? previewPanel = dockControl.GetVisualDescendants().OfType<PreviewPanel>().FirstOrDefault();

        if (editorPanel is null)
        {
            _logger.LogWarning("EditorPanel not found yet - will retry after short delay");
            // Retry with lower priority to allow DataTemplates to apply
            Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Background);
            return;
        }

        // Found EditorPanel - initialize it
        _editor = editorPanel.Editor;
        _logger.LogInformation("EditorPanel found - initializing editor");

        // Initialize syntax highlighting
        InitializeSyntaxHighlighting();

        // Initialize editor with ViewModel data
        SetEditorStateWithValidation(
            _vm.EditorViewModel.DiagramText,
            _vm.EditorViewModel.EditorSelectionStart,
            _vm.EditorViewModel.EditorSelectionLength,
            _vm.EditorViewModel.EditorCaretOffset
        );

        _logger.LogInformation("Editor initialized with {CharacterCount} characters", _vm.EditorViewModel.DiagramText.Length);

        // Wire editor event handlers (now that editor exists)
        WireEditorEventHandlers();

        // Find PreviewPanel
        if (previewPanel is not null)
        {
            _preview = previewPanel.Preview;
            _logger.LogInformation("PreviewPanel found - preview WebView ready");
        }
        else
        {
            _logger.LogWarning("PreviewPanel not found in visual tree");
        }
    }

    /// <summary>
    /// Wires editor event handlers for two-way synchronization with ViewModel.
    /// </summary>
    /// <remarks>
    /// Called after editor control is found and initialized.
    /// Separated from SetupEditorViewModelSync for clearer separation of concerns.
    /// </remarks>
    private void WireEditorEventHandlers()
    {
        if (_editor is null)
        {
            _logger.LogWarning("Cannot wire editor event handlers - editor is null");
            return;
        }

        _logger.LogInformation("Wiring editor event handlers");

        // Subscribe to context menu opening event
        if (_editor.ContextMenu is not null)
        {
            _editor.ContextMenu.Opening += GetContextMenuState;
        }

        // Set up two-way synchronization between Editor and ViewModel
        SetupEditorViewModelSync();
    }

    /// <summary>
    /// Sets the editor text, selection, and caret position while validating bounds and preventing circular updates.
    /// </summary>
    /// <param name="text">The text to set into the editor. Must not be <see langword="null"/>.</param>
    /// <param name="selectionStart">Requested selection start index.</param>
    /// <param name="selectionLength">Requested selection length.</param>
    /// <param name="caretOffset">Requested caret offset.</param>
    private void SetEditorStateWithValidation(string text, int selectionStart, int selectionLength, int caretOffset)
    {
        _suppressEditorStateSync = true; // Prevent circular updates during initialization
        try
        {
            Editor.Text = text;

            // Ensure selection bounds are valid
            int textLength = text.Length;
            int validSelectionStart = Math.Max(0, Math.Min(selectionStart, textLength));
            int validSelectionLength = Math.Max(0, Math.Min(selectionLength, textLength - validSelectionStart));
            int validCaretOffset = Math.Max(0, Math.Min(caretOffset, textLength));
            Editor.SelectionStart = validSelectionStart;
            Editor.SelectionLength = validSelectionLength;
            Editor.CaretOffset = validCaretOffset;

            // Since this is yaml/diagram text, convert tabs to spaces for correct rendering
            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.HighlightCurrentLine = true;
            Editor.Options.IndentationSize = 2;

            _logger.LogInformation("Editor state set with {CharacterCount} characters", textLength);
        }
        finally
        {
            _suppressEditorStateSync = false;
        }
    }

    /// <summary>
    /// Wires up synchronization between the editor control and the view model.
    /// </summary>
    /// <remarks>
    /// - Subscribes to editor text/selection/caret events and updates the view model using a debounce dispatcher.
    /// - Subscribes to view model property changes and applies them to the editor.
    /// - Suppresses reciprocal updates to avoid feedback loops.
    /// </remarks>
    private void SetupEditorViewModelSync()
    {
        // Editor -> ViewModel synchronization (text)
        _editorTextChangedHandler = (_, _) =>
        {
            if (_suppressEditorTextChanged)
            {
                return;
            }

            // Debounce to avoid excessive updates
            _editorDebouncer.DebounceOnUI("editor-text", TimeSpan.FromMilliseconds(DebounceDispatcher.DefaultTextDebounceMilliseconds), () =>
            {
                if (_vm.EditorViewModel.DiagramText != Editor.Text)
                {
                    _suppressEditorStateSync = true;
                    try
                    {
                        _vm.EditorViewModel.DiagramText = Editor.Text;
                    }
                    finally
                    {
                        _suppressEditorStateSync = false;
                    }
                }
            },
            DispatcherPriority.Background);
        };
        Editor.TextChanged += _editorTextChangedHandler;

        // Editor selection/caret -> ViewModel: subscribe to both, coalesce into one update
        _editorSelectionChangedHandler = (_, _) =>
        {
            if (_suppressEditorStateSync)
            {
                return;
            }

            ScheduleEditorStateSyncIfNeeded();
        };
        Editor.TextArea.SelectionChanged += _editorSelectionChangedHandler;

        _editorCaretPositionChangedHandler = (_, _) =>
        {
            if (_suppressEditorStateSync)
            {
                return;
            }

            ScheduleEditorStateSyncIfNeeded();
        };
        Editor.TextArea.Caret.PositionChanged += _editorCaretPositionChangedHandler;

        // ViewModel -> Editor synchronization
        _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;
        _vm.PropertyChanged += _viewModelPropertyChangedHandler;
    }

    /// <summary>
    /// Coalesces caret and selection updates and schedules a debounced update of the view model's editor state.
    /// </summary>
    /// <remarks>
    /// The method compares the current editor state with the view model and only schedules an update
    /// when a change is detected. Values are read again at the time the debounced action runs to coalesce
    /// multiple rapid events into a single update.
    /// </remarks>
    private void ScheduleEditorStateSyncIfNeeded()
    {
        //TODO review this for correctness
        if (_editor is null)
        {
            return;
        }

        int selectionStart = Editor.SelectionStart;
        int selectionLength = Editor.SelectionLength;
        int caretOffset = Editor.CaretOffset;

        if (selectionStart == _vm.EditorViewModel.EditorSelectionStart &&
            selectionLength == _vm.EditorViewModel.EditorSelectionLength &&
            caretOffset == _vm.EditorViewModel.EditorCaretOffset)
        {
            return; // nothing changed
        }

        _editorDebouncer.DebounceOnUI("editor-state", TimeSpan.FromMilliseconds(DebounceDispatcher.DefaultCaretDebounceMilliseconds), () =>
        {
            _suppressEditorStateSync = true;
            try
            {
                // Take the latest values at execution time to coalesce multiple events
                _vm.EditorViewModel.EditorSelectionStart = Editor.SelectionStart;
                _vm.EditorViewModel.EditorSelectionLength = Editor.SelectionLength;
                _vm.EditorViewModel.EditorCaretOffset = Editor.CaretOffset;
            }
            finally
            {
                _suppressEditorStateSync = false;
            }
        },
        DispatcherPriority.Background);
    }

    /// <summary>
    /// Handles property changes on the view model and synchronizes relevant values to the editor control.
    /// </summary>
    /// <param name="sender">The object raising the property changed event (typically the view model).</param>
    /// <param name="e">Property changed event arguments describing which property changed.</param>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        //TODO review this for correctness
        if (_suppressEditorStateSync || _editor is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.DiagramText):
                if (Editor.Text != _vm.EditorViewModel.DiagramText)
                {
                    _editorDebouncer.DebounceOnUI("vm-text", TimeSpan.FromMilliseconds(DebounceDispatcher.DefaultTextDebounceMilliseconds), () =>
                    {
                        _suppressEditorTextChanged = true;
                        _suppressEditorStateSync = true;
                        try
                        {
                            Editor.Text = _vm.EditorViewModel.DiagramText;
                        }
                        finally
                        {
                            _suppressEditorTextChanged = false;
                            _suppressEditorStateSync = false;
                        }
                    },
                    DispatcherPriority.Background);
                }
                break;

            case nameof(EditorViewModel.EditorSelectionStart):
            case nameof(EditorViewModel.EditorSelectionLength):
            case nameof(EditorViewModel.CanCopyClipboard):
            case nameof(EditorViewModel.CanPasteClipboard):
            case nameof(EditorViewModel.EditorCaretOffset):
                _editorDebouncer.DebounceOnUI("vm-selection", TimeSpan.FromMilliseconds(DebounceDispatcher.DefaultCaretDebounceMilliseconds), () =>
                {
                    _suppressEditorStateSync = true;
                    try
                    {
                        // Validate bounds before setting
                        int textLength = Editor.Text.Length;
                        int validSelectionStart = Math.Max(0, Math.Min(_vm.EditorViewModel.EditorSelectionStart, textLength));
                        int validSelectionLength = Math.Max(0, Math.Min(_vm.EditorViewModel.EditorSelectionLength, textLength - validSelectionStart));
                        int validCaretOffset = Math.Max(0, Math.Min(_vm.EditorViewModel.EditorCaretOffset, textLength));

                        if (Editor.SelectionStart != validSelectionStart ||
                            Editor.SelectionLength != validSelectionLength ||
                            Editor.CaretOffset != validCaretOffset)
                        {
                            Editor.SelectionStart = validSelectionStart;
                            Editor.SelectionLength = validSelectionLength;
                            Editor.CaretOffset = validCaretOffset;
                        }
                    }
                    finally
                    {
                        _suppressEditorStateSync = false;
                    }
                },
                DispatcherPriority.Background);
                break;
        }
    }

    #region Lifecycle Overrides

    /// <summary>
    /// Called when the control is attached to the visual tree.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnAttachedToVisualTree ⭐ IMPORTANT
    /// - WIRE ALL EVENT HANDLERS HERE
    /// - Subscribe to window-level events
    /// - Subscribe to ViewModel events
    ///
    /// This is the RIGHT place for ALL event handler wiring!
    /// Cleanup happens automatically in OnDetachedFromVisualTree and OnUnloaded.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _logger.LogInformation("MainWindow attached to visual tree - wiring event handlers");

        // Wire window-level event handlers
        ActualThemeVariantChanged += OnThemeChanged;
        Activated += OnActivated;

        // Wire ViewModel property changed (for two-way sync with EditorViewModel)
        _vm.EditorViewModel.PropertyChanged += _viewModelPropertyChangedHandler = OnViewModelPropertyChanged;

        // Note: Editor/Preview event handlers will be wired when panels are found
        // This happens in WireEditorEventHandlers() called from TryFindDockPanels()
    }

    /// <summary>
    /// Called when the control is fully loaded.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnLoaded ⭐ IMPORTANT
    /// - Initialize heavy resources (WebView)
    /// - Find controls from DataTemplates
    /// - Start async operations
    ///
    /// This is the RIGHT place for resource initialization and finding DataTemplate controls.
    /// </remarks>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _logger.LogInformation("MainWindow loaded - initializing controls");

        // Post to ensure DataTemplates have been applied
        // Uses retry logic if panels not found immediately
        Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Called when the control is being unloaded.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnUnloaded ⭐ CRITICAL
    /// - UNSUBSCRIBE ALL EVENT HANDLERS (prevents memory leaks!)
    /// - Stop timers and animations
    /// - Cancel async operations
    ///
    /// This is the RIGHT place for ALL event handler cleanup!
    /// </remarks>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _logger.LogInformation("MainWindow unloading - cleaning up event handlers");

        // Unsubscribe all event handlers to prevent memory leaks
        UnsubscribeAllEventHandlers();

        base.OnUnloaded(e);
    }

    /// <summary>
    /// Called when the control is detached from the visual tree.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnDetachedFromVisualTree
    /// - Release visual resources
    /// - Dispose WebView
    /// - Clear cached visual elements
    ///
    /// This is the RIGHT place for WebView disposal and graphics cleanup.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _logger.LogInformation("MainWindow detached from visual tree - cleaning up resources");

        // Dispose WebView if it exists
        // Note: WebView disposal is best done here rather than in async cleanup
        if (_preview is not null)
        {
            try
            {
                // WebView cleanup happens here
                _logger.LogInformation("WebView cleanup completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during WebView cleanup");
            }
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Called when the window is opened and visible.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnOpened
    /// - Window is NOW visible to user
    /// - Start async initialization
    /// - Focus initial control
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _logger.LogInformation("MainWindow opened - window is now visible");

        OnOpenedCoreAsync()
            .SafeFireAndForget(onException: ex =>
            {
                _logger.LogError(ex, "Unhandled exception in OnOpened");
            });
    }

    /// <summary>
    /// Handles window activation (gains focus).
    /// </summary>
    /// <remarks>
    /// Fires EVERY time window gains focus.
    /// Brings focus to editor when window becomes active.
    /// </remarks>
    private void OnActivated(object? sender, EventArgs e)
    {
        BringFocusToEditor();
    }

    /// <summary>
    /// Called when the window is closing (can be cancelled).
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE: OnClosing
    /// - Check for unsaved changes
    /// - Show confirmation dialog
    /// - Set e.Cancel = true to prevent closing
    /// - Save window state
    ///
    /// Note: Event handler cleanup happens in OnUnloaded, not here.
    /// </remarks>
    [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Base method guarantees non-null e")]
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        _logger.LogInformation("MainWindow closing requested");

        // Check for unsaved changes (only if not already approved)
        if (!_isClosingApproved && _vm.IsDirty && !string.IsNullOrWhiteSpace(_vm.EditorViewModel.DiagramText))
        {
            e.Cancel = true;
            PromptAndCloseAsync()
                .SafeFireAndForget(onException: ex =>
                {
                    _logger.LogError(ex, "Failed during close prompt");
                    _isClosingApproved = false; // Reset on error
                });
            return; // Don't clean up - close was cancelled
        }

        // Reset approval flag if it was set
        if (_isClosingApproved)
        {
            _isClosingApproved = false;
        }

        // Check if close was cancelled by another handler or the system
        if (e.Cancel)
        {
            return; // Don't clean up - window is not actually closing
        }

        try
        {
            // Save state before closing
            _vm.Persist();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting state during window closing");
            throw;
        }

        // Perform async cleanup
        ILogger<MainWindow> logger = _logger;
        OnClosingAsync()
            .SafeFireAndForget(onException: ex => logger.LogError(ex, "Failed during window close cleanup"));
    }

    #endregion Lifecycle Overrides

    //protected override void OnActualThemeVariantChanged(ActualThemeVariantChangedEventArgs e)
    //    {
    //    base.OnActualThemeVariantChanged(e);
    //    OnThemeChanged(this, EventArgs.Empty);
    //}

    /// <summary>
    /// Brings focus to the editor control and adjusts visuals for caret and selection.
    /// </summary>
    /// <remarks>
    /// This method executes on the UI thread via the dispatcher and temporarily suppresses
    /// editor <see cref="_suppressEditorStateSync"/> to avoid generating spurious model updates.
    /// </remarks>
    private void BringFocusToEditor()
    {
        //TODO review this for correctness - If this is null, then this method was called before the DockControl loaded!!
        if (_editor is null)
        {
            return; // Editor not initialized yet
        }

        Dispatcher.UIThread.Post(() =>
        {
            //TODO review this for correctness - If this is null, then this method was called before the DockControl loaded!!
            if (_editor is null) return; // Double-check after async dispatch

            // Suppress event reactions during programmatic focus/caret adjustments
            _suppressEditorStateSync = true;
            try
            {
                // Make sure caret is visible:
                _editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Colors.Red);

                // Ensure selection is visible
                _editor.TextArea.SelectionBrush = new SolidColorBrush(Colors.SteelBlue);
                if (!_editor.IsFocused)
                {
                    _editor.Focus();
                }
                _editor.TextArea.Caret.BringCaretToView();
            }
            finally
            {
                _suppressEditorStateSync = false;
            }
        }, DispatcherPriority.Background);
    }

    ///// <summary>
    ///// Handles the window <see cref="OnOpened"/> event and starts the asynchronous open sequence.
    ///// </summary>
    ///// <param name="sender">Event sender (window).</param>
    ///// <param name="e">Event arguments (unused).</param>
    ///// <remarks>
    ///// This method delegates to <see cref="OnOpenedCoreAsync"/> to perform asynchronous initialization,
    ///// subscribe to renderer events, and start a failsafe timeout to enable UI if the WebView never becomes ready.
    ///// Uses SafeFireAndForget to handle the async operation without blocking the event handler.
    ///// </remarks>
    //private void OnOpened(object? sender, EventArgs e)
    //{
    //    OnOpenedCoreAsync()
    //        .SafeFireAndForget(onException: ex =>
    //        {
    //            _logger.LogError(ex, "Unhandled exception in OnOpened");
    //            //TODO - show a message to the user (this would need UI thread!)
    //            //Dispatcher.UIThread.Post(async () =>
    //            //{
    //            //    await MessageBox.ShowAsync(this, "An error occurred while opening the window. Please try again.", "Error", MessageBox.MessageBoxButtons.Ok, MessageBox.MessageBoxIcon.Error);
    //            //});
    //        }
    //        //TODO - re-enable this if I add UI operations in the future
    //        //continueOnCapturedContext: true  // Needed for UI operations and event subscriptions
    //        );
    //}

    /// <summary>
    /// Handles the core logic to be executed when the window is opened asynchronously.
    /// </summary>
    /// <remarks>This method logs the window open event, invokes additional asynchronous operations,  and ensures the
    /// editor receives focus. It is intended to be called as part of the  window opening lifecycle.</remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task OnOpenedCoreAsync()
    {
        _suppressEditorStateSync = true;
        try
        {
            await OnOpenedAsync();
            BringFocusToEditor();
        }
        finally
        {
            _suppressEditorStateSync = false;
        }
    }

    /// <summary>
    /// Performs the longer-running open sequence: check for updates, initialize the WebView, and update command states.
    /// </summary>
    /// <returns>A task representing the asynchronous open sequence.</returns>
    /// <exception cref="InvalidOperationException">Thrown if required update assets cannot be resolved.</exception>
    /// <remarks>
    /// This method logs timing information, performs an update check by calling <see cref="MainViewModel.CheckForMermaidUpdatesAsync"/>,
    /// initializes the renderer via <see cref="InitializeWebViewAsync"/>, and notifies commands to refresh their CanExecute state.
    /// Exceptions are propagated for higher-level handling.
    /// </remarks>
    private async Task OnOpenedAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("=== Window Opened Sequence Started ===");

        try
        {
            // TODO - re-enable this once a more complete update mechanism is in place
            // Step 1: Check for Mermaid updates
            //_logger.LogInformation("Step 1: Checking for Mermaid updates...");
            //await _vm.CheckForMermaidUpdatesAsync();
            //_logger.LogInformation("Mermaid update check completed");

            // Step 2: Initialize WebView (editor state is already synchronized via constructor)
            _logger.LogInformation("Step 2: Initializing WebView...");
            string? assetsPath = Path.GetDirectoryName(_updateService.BundledMermaidPath);
            if (assetsPath is null)
            {
                const string error = "BundledMermaidPath does not contain a directory component";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }

            // Needs to be on UI thread
            await InitializeWebViewAsync();

            // Step 3: Update command states
            _logger.LogInformation("Step 3: Updating command states...");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _vm.RenderCommand.NotifyCanExecuteChanged();
                _vm.ClearCommand.NotifyCanExecuteChanged();
            });

            stopwatch.Stop();
            _logger.LogTiming("Window opened sequence", stopwatch.Elapsed, success: true);
            _logger.LogInformation("=== Window Opened Sequence Completed Successfully ===");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogTiming("Window opened sequence", stopwatch.Elapsed, success: false);
            _logger.LogError(ex, "Window opened sequence failed");
            throw;
        }
    }

    /// <summary>
    /// Handles the Click event for the Exit menu item and closes the current window.
    /// </summary>
    /// <param name="sender">The source of the event, typically the Exit menu item.</param>
    /// <param name="e">The event data associated with the Click event.</param>
    [SuppressMessage("ReSharper", "UnusedParameter.Local", Justification = "Event handler signature requires these parameters")]
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    ///// <summary>
    ///// Handles the window closing event, prompting the user to save unsaved changes and performing necessary cleanup
    ///// before the window closes.
    ///// </summary>
    ///// <remarks>If there are unsaved changes, the method prompts the user before allowing the window to
    ///// close. Cleanup and state persistence are only performed if the close operation is not cancelled by this or other
    ///// event handlers.</remarks>
    ///// <param name="sender">The source of the event, typically the window that is being closed.</param>
    ///// <param name="e">A <see cref="CancelEventArgs"/> that contains the event data, including a flag
    ///// to cancel the closing operation.</param>
    //private void OnClosing(object? sender, CancelEventArgs e)
    //{
    //    // Check for unsaved changes (only if not already approved)
    //    if (!_isClosingApproved && _vm.IsDirty && !string.IsNullOrWhiteSpace(_vm.EditorViewModel.DiagramText))
    //    {
    //        e.Cancel = true;
    //        PromptAndCloseAsync()
    //            .SafeFireAndForget(onException: ex =>
    //            {
    //                _logger.LogError(ex, "Failed during close prompt");
    //                _isClosingApproved = false; // Reset on error
    //            });
    //        return; // Don't clean up - close was cancelled
    //    }

    //    // Reset approval flag if it was set
    //    if (_isClosingApproved)
    //    {
    //        _isClosingApproved = false;
    //    }

    //    // Check if close was cancelled by another handler or the system
    //    if (e.Cancel)
    //    {
    //        return; // Don't clean up - window is not actually closing
    //    }

    //    try
    //    {
    //        // Only unsubscribe when we're actually closing (e.Cancel is still false)
    //        UnsubscribeAllEventHandlers();

    //        // Save state
    //        _vm.Persist();
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Error during window closing cleanup");

    //        // I don't want silent failures here - rethrow to let higher-level handlers know
    //        throw;
    //    }

    //    // Perform async cleanup
    //    // Capture logger for use in lambda in case 'this' is disposed before the async work completes
    //    ILogger<MainWindow> logger = _logger;
    //    OnClosingAsync()
    //        .SafeFireAndForget(onException: ex => logger.LogError(ex, "Failed during window close cleanup"));
    //}

    /// <summary>
    /// Unsubscribes all event handlers to prevent memory leaks.
    /// </summary>
    /// <remarks>
    /// Called from OnUnloaded to ensure all event handlers are properly cleaned up.
    /// This prevents memory leaks by breaking references between controls and handlers.
    /// </remarks>
    private void UnsubscribeAllEventHandlers()
    {
        _logger.LogInformation("Unsubscribing all event handlers");

        // Window-level events
        ActualThemeVariantChanged -= OnThemeChanged;
        Activated -= OnActivated;

        // ViewModel events
        if (_viewModelPropertyChangedHandler is not null)
        {
            _vm.EditorViewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
            _viewModelPropertyChangedHandler = null;
        }

        // Editor events (only if editor was initialized)
        if (_editor is not null)
        {
            if (_editorTextChangedHandler is not null)
            {
                _editor.TextChanged -= _editorTextChangedHandler;
                _editorTextChangedHandler = null;
            }

            if (_editorSelectionChangedHandler is not null)
            {
                _editor.TextArea.SelectionChanged -= _editorSelectionChangedHandler;
                _editorSelectionChangedHandler = null;
            }

            if (_editorCaretPositionChangedHandler is not null)
            {
                _editor.TextArea.Caret.PositionChanged -= _editorCaretPositionChangedHandler;
                _editorCaretPositionChangedHandler = null;
            }

            // Context menu event
            if (_editor.ContextMenu is not null)
            {
                _editor.ContextMenu.Opening -= GetContextMenuState;
            }
        }

        _logger.LogInformation("All event handlers unsubscribed successfully");
    }

    /// <summary>
    /// Performs asynchronous cleanup operations when the window is closing.
    /// </summary>
    /// <remarks>This method disposes of resources associated with the window, including any asynchronous
    /// disposable renderer. It should be called during the window closing sequence to ensure proper resource
    /// management.</remarks>
    /// <returns>A task that represents the asynchronous cleanup operation.</returns>
    private async Task OnClosingAsync()
    {
        _logger.LogInformation("Window closing, cleaning up...");

        if (_renderer is IAsyncDisposable disposableRenderer)
        {
            await disposableRenderer.DisposeAsync();
            _logger.LogInformation("MermaidRenderer disposed");
        }

        _logger.LogInformation("Window cleanup completed successfully");
    }

    /// <summary>
    /// Prompts the user to save changes if there are unsaved modifications, and closes the window if the user confirms
    /// or no changes need to be saved.
    /// </summary>
    /// <remarks>If the window is closed, any unsaved changes are either saved or discarded based on the
    /// user's response to the prompt. The method ensures that the close operation does not trigger the save prompt
    /// again. This method should be called when attempting to close the window to prevent accidental loss of unsaved
    /// data.</remarks>
    /// <returns>A task that represents the asynchronous operation. The task completes when the prompt and close sequence has
    /// finished.</returns>
    private async Task PromptAndCloseAsync()
    {
        try
        {
            bool canClose = await _vm.PromptSaveIfDirtyAsync(StorageProvider);
            if (canClose)
            {
                _isClosingApproved = true;
                Close(); // Triggers OnClosing, which resets the flag
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during close prompt");
            _isClosingApproved = false; // Reset on exception
            throw;
        }
    }

    /// <summary>
    /// Initializes the WebView and performs the initial render of the current diagram text.
    /// </summary>
    /// <returns>A task that completes when initialization and initial render have finished.</returns>
    /// <exception cref="OperationCanceledException">Propagated if initialization is canceled.</exception>
    /// <exception cref="AssetIntegrityException">Propagated for asset integrity errors.</exception>
    /// <exception cref="MissingAssetException">Propagated when required assets are missing.</exception>
    /// <remarks>
    /// Temporarily disables live preview while initialization is in progress to prevent unwanted renders.
    /// Performs renderer initialization, waits briefly for content to load, and then triggers an initial render.
    /// Re-enables the live preview setting in a finally block to ensure UI state consistency.
    /// </remarks>
    private async Task InitializeWebViewAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("=== WebView Initialization Started ===");

        // Temporarily disable live preview during WebView initialization
        bool originalLivePreview = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool current = _vm.LivePreviewEnabled;
            _vm.LivePreviewEnabled = false;
            _logger.LogInformation("Temporarily disabled live preview (was: {Current})", current);
            return current;
        }, DispatcherPriority.Normal);

        bool success = false;
        try
        {
            // Step 1: Initialize renderer (starts HTTP server + navigate)
            await _renderer.InitializeAsync(Preview);

            // Step 2: Kick first render; index.html sets globalThis.__renderingComplete__ in hideLoadingIndicator()
            await _renderer.RenderAsync(_vm.EditorViewModel.DiagramText);

            // Step 3: Await readiness
            try
            {
                await _renderer.EnsureFirstRenderReadyAsync(TimeSpan.FromSeconds(WebViewReadyTimeoutSeconds));
                await Dispatcher.UIThread.InvokeAsync(() => _vm.PreviewViewModel.IsWebViewReady = true);
                _logger.LogInformation("WebView readiness observed");
            }
            catch (TimeoutException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _vm.PreviewViewModel.IsWebViewReady = true;
                    _vm.PreviewViewModel.LastError = $"WebView initialization timed out after {WebViewReadyTimeoutSeconds} seconds. Some features may not work correctly.";
                });
                _logger.LogWarning("WebView readiness timed out after {TimeoutSeconds}s; enabling commands with warning", WebViewReadyTimeoutSeconds);
            }

            success = true;
            _logger.LogInformation("=== WebView Initialization Completed Successfully ===");
        }
        catch (OperationCanceledException)
        {
            // Treat cancellations distinctly; still propagate
            _logger.LogInformation("WebView initialization was canceled.");
            throw;
        }
        catch (Exception ex) when (ex is AssetIntegrityException or MissingAssetException)
        {
            // Let asset-related exceptions bubble up for higher-level handling
            throw;
        }
        catch (Exception ex)
        {
            // Log and rethrow so OnOpenedAsync observes the failure and can abort the sequence
            _logger.LogError(ex, "WebView initialization failed");
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogTiming("WebView initialization", stopwatch.Elapsed, success);

            // Re-enable live preview after WebView is ready (or on failure)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _vm.LivePreviewEnabled = originalLivePreview;
                _logger.LogInformation("Re-enabled live preview: {OriginalLivePreview}", originalLivePreview);
            });
        }
    }

    #region Clipboard methods

    /// <summary>
    /// Determines the enabled state of context menu clipboard commands based on the current editor selection and
    /// clipboard availability.
    /// </summary>
    /// <remarks>This method is intended to be used as an event handler for context menu opening events. It
    /// updates the clipboard-related command states to reflect whether copy and paste actions are currently
    /// available.</remarks>
    /// <param name="sender">The source of the event, typically the control that triggered the context menu opening.</param>
    /// <param name="e">A <see cref="CancelEventArgs"/> instance that can be used to cancel the context menu opening.</param>
    [SuppressMessage("ReSharper", "UnusedParameter.Local", Justification = "Event handler signature requires these parameters")]
    private void GetContextMenuState(object? sender, CancelEventArgs e)
    {
        // Get Clipboard state
        _vm.EditorViewModel.CanCopyClipboard = _vm.EditorViewModel.EditorSelectionLength > 0;

        UpdateCanPasteClipboardAsync()
            .SafeFireAndForget(onException: ex => _logger.LogError(ex, "Failed to update CanPasteClipboard"));
    }

    /// <summary>
    /// Asynchronously retrieves the current text content from the clipboard associated with the specified window.
    /// </summary>
    /// <remarks>If the clipboard is unavailable or does not contain text, the method returns null. The
    /// operation is performed on the appropriate UI thread as required by the window's clipboard
    /// implementation.</remarks>
    /// <param name="window">The window whose clipboard is accessed to retrieve text. Must not be null.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the clipboard text if available;
    /// otherwise, null.</returns>
    private static async Task<string?> GetTextFromClipboardAsync(Window window)
    {
        // Access Window.Clipboard on the UI thread
        IClipboard? clipboard = Dispatcher.UIThread.CheckAccess()
            ? window.Clipboard
            : await Dispatcher.UIThread.InvokeAsync(() => window.Clipboard, DispatcherPriority.Background);

        if (clipboard is null)
        {
            return null;
        }

        // Perform the read without capturing the UI context (no UI touched afterward)
        string? clipboardText = await clipboard.TryGetTextAsync()
            .ConfigureAwait(false);
        return clipboardText;
    }

    /// <summary>
    /// Asynchronously updates the ViewModel to reflect whether clipboard text is available for pasting.
    /// </summary>
    /// <remarks>This method reads the clipboard text off the UI thread and updates the CanPasteClipboard
    /// property on the ViewModel. If clipboard access fails or the clipboard contains only whitespace,
    /// CanPasteClipboard is set to false. The update is marshaled back to the UI thread to ensure thread
    /// safety.</remarks>
    /// <returns>
    /// A <see cref="Task"/> that represents the asynchronous operation of updating the ViewModel's
    /// <see cref="MainViewModel.CanPasteClipboard"/> property based on the current clipboard contents.
    /// The task completes when the property has been updated.
    /// </returns>
    private async Task UpdateCanPasteClipboardAsync()
    {
        string? clipboardText = null;

        try
        {
            // Perform clipboard I/O off the UI context
            clipboardText = await GetTextFromClipboardAsync(this)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log and treat as no pasteable text
            _logger.LogError(ex, "Error reading clipboard text");
        }

        bool canPaste = !string.IsNullOrWhiteSpace(clipboardText);

        // Marshal back to UI thread to update the ViewModel property
        await Dispatcher.UIThread.InvokeAsync(() => _vm.EditorViewModel.CanPasteClipboard = canPaste, DispatcherPriority.Normal);
    }

    #endregion Clipboard methods

    #region Syntax Highlighting methods

    /// <summary>
    /// Initializes syntax highlighting for the text editor.
    /// </summary>
    /// <remarks>
    /// This method initializes the syntax highlighting service and applies Mermaid syntax highlighting
    /// to the editor. The theme is automatically selected based on the current Avalonia theme variant.
    /// </remarks>
    private void InitializeSyntaxHighlighting()
    {
        try
        {
            // Initialize the service (verifies grammar resources exist)
            _syntaxHighlightingService.Initialize();

            // Apply Mermaid syntax highlighting with automatic theme detection
            _syntaxHighlightingService.ApplyTo(Editor);

            _logger.LogInformation("Syntax highlighting initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize syntax highlighting");
            // Non-fatal: Continue without syntax highlighting rather than crash the application
        }
    }

    /// <summary>
    /// Handles theme variant changes to update syntax highlighting theme.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    [SuppressMessage("ReSharper", "UnusedParameter.Local", Justification = "Event handler signature requires these parameters")]
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        try
        {
            // Get syntax highlighting service from App.Services
            bool isDarkTheme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

            // Update syntax highlighting theme to match
            _syntaxHighlightingService.UpdateThemeForVariant(isDarkTheme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling theme change");
            // Non-fatal: Continue with current theme
        }
    }

    #endregion Syntax Highlighting methods
}
