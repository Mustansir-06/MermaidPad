// MIT License
// Copyright (c) 2025 Dave Black
// This is a REFACTORED VERSION showing proper Avalonia lifecycle usage
// TODO: Review and replace MainWindow.axaml.cs with this implementation

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaWebView;
using Dock.Avalonia.Controls;
using MermaidPad.Services;
using MermaidPad.Services.Highlighting;
using MermaidPad.ViewModels;
using MermaidPad.Views.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace MermaidPad.Views;

/// <summary>
/// Refactored MainWindow following proper Avalonia lifecycle patterns.
/// Uses overrides instead of events to prevent manual cleanup issues.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Services (resolved in constructor)
    private readonly IDebounceDispatcher _editorDebouncer;
    private readonly MermaidRenderer _renderer;
    private readonly MainViewModel _vm;
    private readonly MermaidUpdateService _updateService;
    private readonly SyntaxHighlightingService _syntaxHighlightingService;
    private readonly ILogger<MainWindow> _logger;

    // Controls (resolved in OnApplyTemplate or later)
    private DockControl? _dockControl;
    private TextEditor? _editor;
    private WebView? _preview;

    // State flags
    private bool _isClosingApproved;
    private bool _suppressEditorTextChanged;
    private bool _suppressEditorStateSync;

    #region 1. Constructor - Resolve Services, Set DataContext

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE 1: Constructor
    /// - Call InitializeComponent() FIRST
    /// - Resolve services from DI
    /// - Set DataContext
    /// - Initialize simple fields
    ///
    /// DO NOT:
    /// - Access controls (they're null!)
    /// - Wire event handlers (do in OnAttachedToVisualTree)
    /// - Initialize heavy resources (do in OnLoaded)
    /// </remarks>
    public MainWindow()
    {
        InitializeComponent();

        // Resolve services from DI
        IServiceProvider sp = App.Services;
        _editorDebouncer = sp.GetRequiredService<IDebounceDispatcher>();
        _renderer = sp.GetRequiredService<MermaidRenderer>();
        _vm = sp.GetRequiredService<MainViewModel>();
        _updateService = sp.GetRequiredService<MermaidUpdateService>();
        _syntaxHighlightingService = sp.GetRequiredService<SyntaxHighlightingService>();
        _logger = sp.GetRequiredService<ILogger<MainWindow>>();

        // Set DataContext
        DataContext = _vm;

        _logger.LogInformation("MainWindow constructor completed");
    }

    #endregion

    #region 2. OnApplyTemplate - Get Template Children

    /// <summary>
    /// Called when the control template is applied.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE 2: OnApplyTemplate
    /// - Get references to template children
    /// - Cache control references
    /// - Verify required controls exist
    ///
    /// DO NOT:
    /// - Wire event handlers yet (do in OnAttachedToVisualTree)
    /// - Access control properties (they're still initializing)
    /// </remarks>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Find DockControl from template
        _dockControl = this.FindControl<DockControl>("MainDock");

        if (_dockControl is null)
        {
            _logger.LogError("DockControl 'MainDock' not found in template");
        }
        else
        {
            _logger.LogInformation("DockControl found in template");
        }
    }

    #endregion

    #region 3. OnAttachedToVisualTree - Wire Event Handlers

    /// <summary>
    /// Called when the control is attached to the visual tree.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE 3: OnAttachedToVisualTree ⭐ IMPORTANT
    /// - WIRE ALL EVENT HANDLERS HERE
    /// - Subscribe to control events
    /// - Subscribe to ViewModel events
    /// - Setup property change listeners
    ///
    /// This is the RIGHT place for ALL event handler wiring!
    /// Automatic cleanup happens in OnDetachedFromVisualTree.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _logger.LogInformation("MainWindow attached to visual tree - wiring event handlers");

        // Wire window-level event handlers
        ActualThemeVariantChanged += OnThemeChanged;

        // Wire ViewModel property changed (for two-way sync)
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // Note: Editor/Preview event handlers will be wired when panels are found
        // This happens in TryFindDockPanels() called from OnLoaded
    }

    #endregion

    #region 4. OnLoaded - Initialize Heavy Resources

    /// <summary>
    /// Called when the control is fully loaded.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE 4: OnLoaded ⭐ IMPORTANT
    /// - Initialize heavy resources (WebView, etc.)
    /// - Load initial data
    /// - Start async operations
    /// - Find controls from DataTemplates (they should exist by now)
    ///
    /// This is the RIGHT place for:
    /// - WebView initialization
    /// - File loading
    /// - Finding DataTemplate controls
    /// </remarks>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _logger.LogInformation("MainWindow loaded - initializing controls and resources");

        // Try to find dock panels from DataTemplates
        // DataTemplates should be applied by now, but might need a short delay
        Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Attempts to find EditorPanel and PreviewPanel from DockControl DataTemplates.
    /// </summary>
    /// <remarks>
    /// DataTemplates in DockControl are applied lazily. This method searches the visual tree
    /// for the panels and wires up their event handlers.
    ///
    /// Called via Dispatcher.Post to ensure DataTemplates have been fully applied.
    /// </remarks>
    private void TryFindDockPanels()
    {
        if (_dockControl is null)
        {
            _logger.LogError("Cannot find dock panels - DockControl is null");
            return;
        }

        _logger.LogInformation("Searching for EditorPanel and PreviewPanel in visual tree");

        // Find EditorPanel from DataTemplate
        EditorPanel? editorPanel = _dockControl.GetVisualDescendants().OfType<EditorPanel>().FirstOrDefault();
        if (editorPanel is not null)
        {
            _editor = editorPanel.Editor;
            _logger.LogInformation("EditorPanel found - initializing editor");

            // Initialize editor with ViewModel data
            InitializeEditor();

            // Wire editor event handlers
            WireEditorEventHandlers();
        }
        else
        {
            _logger.LogWarning("EditorPanel not found in visual tree - will retry");
            // Retry after a short delay (DataTemplate might still be applying)
            Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Background);
            return;
        }

        // Find PreviewPanel from DataTemplate
        PreviewPanel? previewPanel = _dockControl.GetVisualDescendants().OfType<PreviewPanel>().FirstOrDefault();
        if (previewPanel is not null)
        {
            _preview = previewPanel.Preview;
            _logger.LogInformation("PreviewPanel found - initializing WebView");

            // Initialize WebView
            InitializeWebView();
        }
        else
        {
            _logger.LogWarning("PreviewPanel not found in visual tree");
        }
    }

    /// <summary>
    /// Initializes the editor control with ViewModel data.
    /// </summary>
    private void InitializeEditor()
    {
        if (_editor is null) return;

        _logger.LogInformation("Initializing editor with {CharacterCount} characters", _vm.DiagramText.Length);

        // Initialize syntax highlighting
        InitializeSyntaxHighlighting();

        // Set editor state from ViewModel
        SetEditorStateWithValidation(
            _vm.DiagramText,
            _vm.EditorSelectionStart,
            _vm.EditorSelectionLength,
            _vm.EditorCaretOffset
        );
    }

    /// <summary>
    /// Wires editor event handlers for two-way synchronization with ViewModel.
    /// </summary>
    private void WireEditorEventHandlers()
    {
        if (_editor is null) return;

        _logger.LogInformation("Wiring editor event handlers");

        // Wire editor events
        _editor.TextChanged += OnEditorTextChanged;
        _editor.SelectionChanged += OnEditorSelectionChanged;
        _editor.CaretPositionChanged += OnEditorCaretPositionChanged;

        // Wire context menu
        if (_editor.ContextMenu is not null)
        {
            _editor.ContextMenu.Opening += OnContextMenuOpening;
        }
    }

    /// <summary>
    /// Initializes syntax highlighting for the editor.
    /// </summary>
    private void InitializeSyntaxHighlighting()
    {
        if (_editor is null) return;

        try
        {
            _syntaxHighlightingService.ApplySyntaxHighlighting(_editor, ActualThemeVariant);
            _logger.LogInformation("Syntax highlighting initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize syntax highlighting");
        }
    }

    /// <summary>
    /// Initializes the WebView control.
    /// </summary>
    private void InitializeWebView()
    {
        if (_preview is null) return;

        _logger.LogInformation("Initializing WebView");
        // TODO: Add WebView initialization logic here
    }

    #endregion

    #region 5. OnOpened - Window Is Now Visible

    /// <summary>
    /// Called when the window is opened and visible.
    /// </summary>
    /// <remarks>
    /// LIFECYCLE PHASE 5: OnOpened
    /// - Window is NOW visible to the user
    /// - Focus initial control
    /// - Start animations
    /// - Show welcome dialogs
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _logger.LogInformation("MainWindow opened - window is now visible");

        // Bring focus to editor
        BringFocusToEditor();
    }

    /// <summary>
    /// Brings focus to the editor control if it exists.
    /// </summary>
    private void BringFocusToEditor()
    {
        if (_editor is not null)
        {
            _editor.Focus();
            _logger.LogDebug("Editor focused");
        }
    }

    #endregion

    #region Event Handlers (Wired in OnAttachedToVisualTree)

    /// <summary>
    /// Handles theme changes.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("Theme changed to {Theme}", ActualThemeVariant);
        InitializeSyntaxHighlighting();
    }

    /// <summary>
    /// Handles ViewModel property changes for two-way synchronization.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Handle ViewModel → View synchronization
        // TODO: Implement property sync logic
    }

    /// <summary>
    /// Handles editor text changes.
    /// </summary>
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorTextChanged) return;

        // TODO: Update ViewModel.DiagramText
        // TODO: Trigger debounced render
    }

    /// <summary>
    /// Handles editor selection changes.
    /// </summary>
    private void OnEditorSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorStateSync || _editor is null) return;

        // TODO: Update ViewModel selection properties
    }

    /// <summary>
    /// Handles editor caret position changes.
    /// </summary>
    private void OnEditorCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorStateSync || _editor is null) return;

        // TODO: Update ViewModel caret offset
    }

    /// <summary>
    /// Handles context menu opening.
    /// </summary>
    private void OnContextMenuOpening(object? sender, EventArgs e)
    {
        // TODO: Update CanCopy/CanPaste in ViewModel
    }

    #endregion

    #region Shutdown Sequence

    /// <summary>
    /// Called when the window is closing (can be cancelled).
    /// </summary>
    /// <remarks>
    /// SHUTDOWN PHASE 1: OnClosing
    /// - Check for unsaved changes
    /// - Show confirmation dialog
    /// - Set e.Cancel = true to prevent closing
    /// - Save window state
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _logger.LogInformation("MainWindow closing requested");

        // Check for unsaved changes
        if (_vm.IsDirty && !_isClosingApproved)
        {
            // TODO: Show unsaved changes dialog
            // e.Cancel = true; // If user cancels
        }

        // Persist state
        _vm.Persist();

        base.OnClosing(e);
    }

    /// <summary>
    /// Called when the window is closed (cannot be cancelled).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _logger.LogInformation("MainWindow closed");
        base.OnClosed(e);
    }

    /// <summary>
    /// Called when the control is unloaded.
    /// </summary>
    /// <remarks>
    /// SHUTDOWN PHASE 2: OnUnloaded ⭐ CRITICAL
    /// - UNSUBSCRIBE ALL EVENT HANDLERS (prevents memory leaks!)
    /// - Stop timers
    /// - Cancel async operations
    ///
    /// This is the RIGHT place for ALL event handler cleanup!
    /// </remarks>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _logger.LogInformation("MainWindow unloading - cleaning up event handlers");

        // Unsubscribe window-level events
        ActualThemeVariantChanged -= OnThemeChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;

        // Unsubscribe editor events
        if (_editor is not null)
        {
            _editor.TextChanged -= OnEditorTextChanged;
            _editor.SelectionChanged -= OnEditorSelectionChanged;
            _editor.CaretPositionChanged -= OnEditorCaretPositionChanged;

            if (_editor.ContextMenu is not null)
            {
                _editor.ContextMenu.Opening -= OnContextMenuOpening;
            }
        }

        base.OnUnloaded(e);
    }

    /// <summary>
    /// Called when the control is detached from the visual tree.
    /// </summary>
    /// <remarks>
    /// SHUTDOWN PHASE 3: OnDetachedFromVisualTree
    /// - Release visual resources
    /// - Dispose WebView
    /// - Clear cached visual elements
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _logger.LogInformation("MainWindow detached from visual tree - cleaning up resources");

        // Dispose WebView
        if (_preview is not null)
        {
            // TODO: Dispose WebView properly
        }

        base.OnDetachedFromVisualTree(e);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Sets the editor text, selection, and caret position with validation.
    /// </summary>
    private void SetEditorStateWithValidation(string text, int selectionStart, int selectionLength, int caretOffset)
    {
        if (_editor is null) return;

        _suppressEditorStateSync = true;
        try
        {
            _editor.Text = text;

            // Validate and set selection
            int textLength = text.Length;
            int validSelectionStart = Math.Max(0, Math.Min(selectionStart, textLength));
            int validSelectionLength = Math.Max(0, Math.Min(selectionLength, textLength - validSelectionStart));
            int validCaretOffset = Math.Max(0, Math.Min(caretOffset, textLength));

            _editor.SelectionStart = validSelectionStart;
            _editor.SelectionLength = validSelectionLength;
            _editor.CaretOffset = validCaretOffset;
        }
        finally
        {
            _suppressEditorStateSync = false;
        }
    }

    #endregion
}
