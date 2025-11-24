# Dock.Avalonia Debug Analysis

## Current Problems

### 1. Panels Not Visible (Black Screen)
**Status**: Panels exist in memory but not rendering

### 2. JSON Serialization Error
```
A possible object cycle was detected. This can either be due to a cycle or if the object depth is larger than the maximum allowed depth of 64.
Path: $.Context.EditorViewModel
```

## Root Cause Analysis

### Issue 1: Initialization Order Problem

**Current Flow** (App.axaml.cs:110-132):
```csharp
Services = ServiceConfiguration.BuildServiceProvider();          // Line 115
MainViewModel mainViewModel = Services.GetRequiredService<MainViewModel>();  // Line 121 ⚠️
MainWindow mainWindow = new MainWindow(...) { DataContext = mainViewModel };  // Line 123-132
desktop.MainWindow = mainWindow;                                  // Line 141
```

**The Problem**:
- MainViewModel is created **BEFORE** MainWindow exists
- MainViewModel.constructor calls `LoadLayout()` (line 253)
- `LoadLayout()` creates the dock layout structure
- But there's **no visual tree to attach to yet**!

**In MainViewModel.cs constructor** (lines 248-253):
```csharp
InitializeContextLocator();  // Sets up ViewModel mappings
LoadLayout();                 // Creates/loads dock structure ⚠️ NO WINDOW YET!
```

**What happens**:
1. Layout is created in memory
2. Panels (EditorTool, PreviewTool, AiTool) exist
3. ContextLocator maps them to ViewModels
4. BUT no MainWindow exists to render them
5. When MainWindow is created later, the binding `Layout="{Binding Layout}"` should work
6. But the DockControl might not trigger LayoutUpdated if Layout is already set before it's created

### Issue 2: JSON Serialization Circular References

**The Problem**:
When `_dockSerializer.Save(stream, Layout)` runs, it tries to serialize:
- The entire dock layout tree
- Each Tool has a **Context** property
- Context is set to ViewModels (EditorViewModel, PreviewViewModel, AIPanelViewModel)
- ViewModels contain references to other objects
- This creates circular references

**Why Context shouldn't be serialized**:
- Context is **runtime-only** data
- It's set dynamically via ContextLocator when the layout loads
- The serializer should only save structural data (panel IDs, positions, sizes)
- NOT the actual ViewModel instances

## Diagnostic Questions

### Q1: Should we disable layout saving/loading temporarily?
**Answer**: YES - this will help us debug the visibility issue without the JSON error interfering

### Q2: Is MainViewModel/MainWindow creation order the problem?
**Answer**: LIKELY - MainViewModel creates the layout before MainWindow exists to render it

### Q3: Lifecycle events - missing or wrong order?
**Answer**: The OnLoaded event wiring looks correct, but we need to verify:
- Is OnLoaded actually firing?
- Is LayoutUpdated firing?
- Are panels being discovered?

## Proposed Solutions

### Solution 1: Temporarily Disable Layout Persistence (RECOMMENDED FIRST STEP)

**Why**: Isolate the visibility problem from the serialization problem

**How**: In MainViewModel.cs
```csharp
public void LoadLayout()
{
    // TEMPORARY: Skip loading from file, always create default
    _logger.LogInformation("Creating default layout (persistence disabled for debugging)");
    IRootDock? layout = _dockFactory.CreateLayout();
    if (layout is not null)
    {
        _dockFactory.InitLayout(layout);
    }
    Layout = layout;
}

public void SaveLayout()
{
    // TEMPORARY: Disable saving for debugging
    _logger.LogInformation("SaveLayout called but disabled for debugging");
    return;
}
```

### Solution 2: Add Comprehensive Logging

Add logging to track initialization flow:

**In App.axaml.cs OnFrameworkInitializationCompleted**:
```csharp
Services = ServiceConfiguration.BuildServiceProvider();
logger.LogInformation("=== Creating MainViewModel ===");
MainViewModel mainViewModel = Services.GetRequiredService<MainViewModel>();
logger.LogInformation("=== MainViewModel created, Layout={HasLayout} ===", mainViewModel.Layout != null);

logger.LogInformation("=== Creating MainWindow ===");
MainWindow mainWindow = new MainWindow(...);
logger.LogInformation("=== MainWindow created ===");
```

**In MainViewModel.LoadLayout()**:
```csharp
_logger.LogInformation("LoadLayout START");
// ... existing code ...
_logger.LogInformation("LoadLayout END - Layout created: {LayoutType}, RootDock: {RootId}",
    Layout?.GetType().Name, (Layout as IRootDock)?.Id);
```

**In MainWindow.OnLoaded()**:
```csharp
_logger.LogInformation("MainWindow.OnLoaded - MainDock.Layout = {HasLayout}", MainDock?.Layout != null);
```

**In MainWindow.OnDockControlLayoutUpdated()**:
```csharp
_logger.LogInformation("LayoutUpdated fired - Searching for panels...");
```

### Solution 3: Fix JSON Serialization (For Later)

**Option A**: Configure DockSerializer to ignore Context property
```csharp
// Need to check if Dock.Serializer.SystemTextJson has configuration options
```

**Option B**: Use ReferenceHandler.Preserve
```csharp
// In ServiceConfiguration when registering DockSerializer
// (May not be possible with current Dock library API)
```

**Option C**: Don't serialize Context at all
```csharp
// The library should already do this, but it's not
// May be a bug in Dock.Avalonia or misconfiguration
```

## Debugging Steps

### Step 1: Disable Layout Persistence
1. Comment out LoadLayout/SaveLayout content temporarily
2. Always create default layout
3. Test if panels appear

### Step 2: Check Debug Logs
Look for:
- "MainWindow loaded - wiring DockControl LayoutUpdated event"
- "DockControl LayoutUpdated - Panel discovery: EditorPanel=..."
- "DockControl LayoutUpdated - panels found, initializing"

### Step 3: Verify Layout Property
In MainWindow.OnLoaded, check:
```csharp
_logger.LogInformation("OnLoaded: ViewModel.Layout={Layout}, MainDock.Layout={DockLayout}",
    ViewModel?.Layout?.GetType().Name,
    MainDock?.Layout?.GetType().Name);
```

### Step 4: Check if DataTemplate Resolution is Working
Verify DataTemplates in App.axaml are registered correctly for:
- EditorViewModel → EditorPanel
- PreviewViewModel → PreviewPanel
- AIPanelViewModel → AIPanel

## Expected Initialization Flow (Corrected)

```
1. App.OnFrameworkInitializationCompleted()
   ├─> ServiceConfiguration.BuildServiceProvider()
   ├─> Get MainViewModel from DI
   │   ├─> MainViewModel.constructor()
   │   │   ├─> InitializeContextLocator()  ✓ Safe - just sets up dictionaries
   │   │   └─> LoadLayout()                ⚠️ Creates layout, but no window yet!
   │   └─> Returns with Layout property set
   ├─> Create MainWindow(...)
   │   └─> Set DataContext = mainViewModel
   └─> Set desktop.MainWindow = mainWindow
2. MainWindow initialization
   ├─> XAML parsing
   ├─> DockControl created
   │   └─> Binding: Layout="{Binding Layout}"  ← Should bind to mainViewModel.Layout
   └─> OnLoaded() fires
       ├─> Wire LayoutUpdated event handler
       └─> DockControl.LayoutUpdated fires (multiple times)
           ├─> Search for EditorPanel, PreviewPanel, AIPanel in visual tree
           ├─> If found: Initialize panels
           └─> Unsubscribe from LayoutUpdated
```

## Questions to Answer

1. ✅ Is ContextLocator configured before InitLayout? YES (line 249 before 253)
2. ❓ Does DockControl.Layout binding work when Layout is set before DockControl exists?
3. ❓ Does LayoutUpdated fire if Layout is already set?
4. ❓ Are DataTemplates being applied correctly?
5. ❓ What does the debug.log show for panel discovery?

## Next Actions

**IMMEDIATE**:
1. Disable LoadLayout/SaveLayout (just create default layout)
2. Add comprehensive logging
3. Run app and collect logs
4. Look for OnLoaded and LayoutUpdated log messages

**IF PANELS STILL NOT VISIBLE**:
1. Check if MainDock.Layout is actually bound
2. Check if DataTemplates are resolving
3. Verify ContextLocator is finding ViewModels

**ONCE PANELS VISIBLE**:
1. Re-enable layout loading (but not saving yet)
2. Test if loading works
3. Fix JSON serialization (Context property issue)
4. Re-enable saving
