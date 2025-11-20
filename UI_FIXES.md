# UI Fixes: Status Messages and Build Output Scrolling

## Issues Fixed

### 1. Question Marks in Status Messages

**Problem**: Status messages were showing question marks (`?`) at the beginning instead of the intended icons.

**Root Cause**: The code was using Unicode emoji characters (?? for searching, ? for success, ? for failure) that weren't rendering properly in the WPF TextBlock, displaying as `?` instead.

**Solution**: Replaced emoji characters with plain text or appropriate Unicode characters that render correctly in WPF.

#### Changes Made

**File: `ViewModels\MainViewModel.cs`**
- `StatusMessage = "?? Detecting compilers..."` ? `StatusMessage = "Detecting compilers..."`
- `StatusMessage = $"? Found {count} compiler(s)"` ? `StatusMessage = $"Found {count} compiler(s)"`
- `StatusMessage = "? No compilers found"` ? `StatusMessage = "No compilers found"`
- `StatusMessage = "? Please select a compiler first"` ? `StatusMessage = "Please select a compiler first"`
- `StatusMessage = "?? Detecting optimal CPU architecture..."` ? `StatusMessage = "Detecting optimal CPU architecture..."`
- `StatusMessage = $"? Detected: {name}"` ? `StatusMessage = $"Detected: {name}"`
- `StatusMessage = $"? Error detecting..."` ? `StatusMessage = $"Error detecting..."`

**File: `ViewModels\BuildViewModel.cs`**
- `BuildOutput += "? Compilation successful!\n"` ? `BuildOutput += "? Compilation successful!\n"`
- `BuildOutput += "? Compilation failed!\n"` ? `BuildOutput += "? Compilation failed!\n"`

Note: The ? and ? characters are standard Unicode check mark and ballot X that render properly in most fonts.

---

### 2. Build Output Not Auto-Scrolling to Bottom

**Problem**: When build output was added to the TextBox, it didn't automatically scroll to show the most recent line. Users had to manually scroll down to see new output.

**Root Cause**: WPF TextBox doesn't auto-scroll by default when text is appended programmatically.

**Solution**: Created an attached behavior `TextBoxHelper.AlwaysScrollToEnd` that automatically scrolls the TextBox to the end whenever text changes.

#### Implementation

**New File: `Helpers\TextBoxHelper.cs`**
```csharp
public static class TextBoxHelper
{
    public static readonly DependencyProperty AlwaysScrollToEndProperty =
        DependencyProperty.RegisterAttached(
            "AlwaysScrollToEnd",
            typeof(bool),
            typeof(TextBoxHelper),
            new PropertyMetadata(false, OnAlwaysScrollToEndChanged));

    private static void OnAlwaysScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox && (bool)e.NewValue)
        {
            textBox.TextChanged += TextBox_TextChanged;
        }
    }

    private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
```

**Updated File: `Views\BuildProgressView.xaml`**
```xml
<!-- Added namespace -->
xmlns:helpers="clr-namespace:StockfishCompiler.Helpers"

<!-- Added attached property to TextBox -->
<TextBox helpers:TextBoxHelper.AlwaysScrollToEnd="True" ... />
```

---

## Benefits

1. **Better UX**: Status messages are now clear and readable without confusing question marks
2. **Auto-scrolling**: Users always see the latest build output without manual scrolling
3. **Cleaner code**: Avoided emoji characters that don't render consistently across systems
4. **Reusable**: The `TextBoxHelper` can be used anywhere in the app that needs auto-scrolling behavior

---

## Testing

1. **Status Messages**: Run the app and click "Detect Compilers" and "Detect Optimal Architecture" - you should see clean status messages without `?` characters
2. **Auto-scroll**: Start a build and watch the output - it should automatically show the bottom/most recent lines as the build progresses

---

## Files Modified

1. `ViewModels\MainViewModel.cs` - Removed emoji from status messages
2. `ViewModels\BuildViewModel.cs` - Fixed compilation result messages
3. `Helpers\TextBoxHelper.cs` - NEW: Created attached behavior for auto-scrolling
4. `Views\BuildProgressView.xaml` - Applied auto-scroll behavior to build output TextBox
