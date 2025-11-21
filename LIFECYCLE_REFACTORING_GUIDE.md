# MainWindow Lifecycle Refactoring Guide

## 🚨 Current Problems in MainWindow.axaml.cs

### 1. Event Handlers in Constructor (❌ WRONG)
**Lines 118-122:**
```csharp
_themeChangedHandler = OnThemeChanged;
ActualThemeVariantChanged += _themeChangedHandler;

_activatedHandler = (_, _) => BringFocusToEditor();
Activated += _activatedHandler;
```

**Problem:** Event handlers should be wired in `OnAttachedToVisualTree`, not constructor.

**Issue:** Requires manual cleanup, risk of memory leaks if cleanup is missed.

### 2. Using `Loaded` Event Instead of Override (❌ WRONG)
**Line 125:**
```csharp
Loaded += OnWindowLoaded;
```

**Problem:** Should use `OnLoaded()` override, not event subscription.

**Issue:** Requires manual cleanup, less clean than using overrides.

### 3. Finding Controls Too Early (❌ TIMING ISSUE)
**Lines 146-147 in `OnWindowLoaded`:**
```csharp
EditorPanel? editorPanel = dockControl.GetVisualDescendants().OfType<EditorPanel>().FirstOrDefault();
PreviewPanel? previewPanel = dockControl.GetVisualDescendants().OfType<PreviewPanel>().FirstOrDefault();
```

**Problem:** DataTemplate controls might not exist yet when `Loaded` fires.

**Issue:** Returns `null` because DockControl DataTemplates are applied lazily.

### 4. Wiring Events in Wrong Place (❌ WRONG)
**Lines 157, 174 in `OnWindowLoaded`:**
```csharp
_editor.ContextMenu.Opening += GetContextMenuState;
// ...
SetupEditorViewModelSync(); // This wires even more event handlers!
```

**Problem:** Event handlers should be wired in `OnAttachedToVisualTree`.

**Issue:** Inconsistent timing, harder to track what's wired where.

## ✅ Correct Lifecycle Implementation

### Phase 1: Constructor
**What to do:**
- `InitializeComponent()` - MUST be first
- Resolve services from DI
- Set `DataContext`
- Initialize simple fields

**What NOT to do:**
- ❌ Access controls (they're null!)
- ❌ Wire event handlers
- ❌ Initialize heavy resources

### Phase 2: OnApplyTemplate (Override)
**What to do:**
- Get references to template children
- Cache control references (`_dockControl = this.FindControl<>("MainDock")`)
- Verify required controls exist

**What NOT to do:**
- ❌ Wire event handlers (not yet!)
- ❌ Access control properties (still initializing)

### Phase 3: OnAttachedToVisualTree (Override) ⭐
**What to do:**
- **WIRE ALL EVENT HANDLERS HERE**
- Subscribe to control events
- Subscribe to ViewModel events
- Setup property change listeners

**This is the RIGHT place for ALL event handler wiring!**

```csharp
protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
{
    base.OnAttachedToVisualTree(e);

    // Wire window-level events
    ActualThemeVariantChanged += OnThemeChanged;

    // Wire ViewModel events
    _vm.PropertyChanged += OnViewModelPropertyChanged;
}
```

### Phase 4: OnLoaded (Override) ⭐
**What to do:**
- Initialize heavy resources (WebView)
- Load initial data
- Find DataTemplate controls
- Start async operations

**DataTemplate Control Search:**
```csharp
protected override void OnLoaded(RoutedEventArgs e)
{
    base.OnLoaded(e);

    // Post to dispatcher to ensure DataTemplates are applied
    Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Loaded);
}

private void TryFindDockPanels()
{
    EditorPanel? editorPanel = _dockControl?.GetVisualDescendants()
        .OfType<EditorPanel>()
        .FirstOrDefault();

    if (editorPanel is not null)
    {
        _editor = editorPanel.Editor;
        InitializeEditor();
        WireEditorEventHandlers();
    }
    else
    {
        // Retry after short delay if not found
        Dispatcher.UIThread.Post(TryFindDockPanels, DispatcherPriority.Background);
    }
}
```

### Phase 5: OnOpened (Override)
**What to do:**
- Window is NOW visible
- Focus initial control
- Start animations
- Show welcome dialogs

```csharp
protected override void OnOpened(EventArgs e)
{
    base.OnOpened(e);
    _editor?.Focus();
}
```

## 🧹 Cleanup Phases

### Shutdown Phase 1: OnClosing (Override)
**What to do:**
- Check for unsaved changes
- Show confirmation dialog
- Set `e.Cancel = true` to prevent closing
- Save window state
- Persist data

```csharp
protected override void OnClosing(WindowClosingEventArgs e)
{
    if (_vm.IsDirty && !_isClosingApproved)
    {
        // Show dialog, maybe cancel
        // e.Cancel = true;
    }

    _vm.Persist();
    base.OnClosing(e);
}
```

### Shutdown Phase 2: OnUnloaded (Override) ⭐ CRITICAL
**What to do:**
- **UNSUBSCRIBE ALL EVENT HANDLERS** (prevents memory leaks!)
- Stop timers
- Cancel async operations

**This is the RIGHT place for ALL event handler cleanup!**

```csharp
protected override void OnUnloaded(RoutedEventArgs e)
{
    // Unsubscribe window events
    ActualThemeVariantChanged -= OnThemeChanged;
    _vm.PropertyChanged -= OnViewModelPropertyChanged;

    // Unsubscribe editor events
    if (_editor is not null)
    {
        _editor.TextChanged -= OnEditorTextChanged;
        _editor.SelectionChanged -= OnEditorSelectionChanged;
        // ... etc
    }

    base.OnUnloaded(e);
}
```

### Shutdown Phase 3: OnDetachedFromVisualTree (Override)
**What to do:**
- Release visual resources
- Dispose WebView
- Clear cached visual elements

```csharp
protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
{
    // Dispose WebView
    _preview?.Dispose();

    base.OnDetachedFromVisualTree(e);
}
```

## 📋 Refactoring Checklist

- [ ] Move event handler wiring from constructor to `OnAttachedToVisualTree`
- [ ] Replace `Loaded +=` event with `OnLoaded()` override
- [ ] Replace `Opened +=` event with `OnOpened()` override
- [ ] Replace `Closing +=` event with `OnClosing()` override
- [ ] Add `OnApplyTemplate()` override to find DockControl early
- [ ] Add `OnAttachedToVisualTree()` override to wire all events
- [ ] Use `Dispatcher.Post()` in `OnLoaded()` to find DataTemplate controls
- [ ] Add retry logic if DataTemplate controls not found immediately
- [ ] Add `OnUnloaded()` override to unsubscribe ALL events
- [ ] Add `OnDetachedFromVisualTree()` override to dispose WebView
- [ ] Remove all manual event handler storage fields (not needed with overrides)
- [ ] Test that `_editor` and `_preview` are found correctly
- [ ] Verify no memory leaks with unsubscribed events

## 🎯 Key Benefits

1. **Automatic Cleanup** - Overrides are paired (Attached/Detached, Loaded/Unloaded)
2. **No Memory Leaks** - OnUnloaded ensures all events are unsubscribed
3. **Correct Timing** - DataTemplate controls found after they exist
4. **Cleaner Code** - No manual event handler storage fields needed
5. **Best Practice** - Follows Avalonia recommended patterns

## 📚 Reference

See `MainWindow_Refactored.cs` for complete implementation showing all phases.
