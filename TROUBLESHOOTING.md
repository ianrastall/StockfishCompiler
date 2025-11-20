# Troubleshooting Fixed ?

## Problem
Application wouldn't start after publish - no visible error, just silent failure.

## Root Causes Found

### 1. Resource Loading Issue
**Problem**: `DarkTheme.xaml` resources weren't accessible to views.
**Error in logs**: `Cannot find resource named 'DarkGroupBox'`
**Fix**: Moved resource dictionary to `App.xaml` as merged dictionary instead of window-level resource.

### 2. Single-File Publish Issues
**Problem**: WPF applications don't always work well with PublishSingleFile=true due to resource loading.
**Fix**: Changed to framework-dependent deployment with separate DLLs.

## Solutions Implemented

### 1. Added Comprehensive Logging (Serilog)
- **Location**: `%LOCALAPPDATA%\StockfishCompiler\logs\app-YYYY-MM-DD.log`
- **Features**:
  - Logs all application startup steps
  - Logs service initialization
  - Logs all user actions (detect compilers, build, etc.)
  - Catches and logs all exceptions
  - Shows log file location in error dialogs

### 2. Fixed Resource Loading
**Before** (MainWindow.xaml):
```xml
<Window.Resources>
    <ResourceDictionary Source="Resources/DarkTheme.xaml"/>
</Window.Resources>
```

**After** (App.xaml):
```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Resources/DarkTheme.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

### 3. Changed Publish Configuration
**Before** (.csproj):
```xml
<!-- Single-file, self-contained -->
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
```

**After** (.csproj):
```xml
<!-- Framework-dependent with DLLs -->
<PublishSingleFile>false</PublishSingleFile>
<SelfContained>false</SelfContained>
```

### 4. Added Troubleshooting Tools

#### A. In-App Log Access
- Added "Open Log Folder" button in the app footer
- Instantly opens logs directory in Windows Explorer

#### B. OpenLogs.bat
```batch
explorer "%LOCALAPPDATA%\StockfishCompiler\logs"
```
Standalone batch file to open logs without running the app.

#### C. BuildRelease.bat
Automated script that:
1. Kills running instances
2. Cleans old builds
3. Publishes release build
4. Copies files to `Release\` folder
5. Includes README and OpenLogs.bat

### 5. Error Handling Improvements
```csharp
// Global exception handler in App.xaml.cs
private void Application_DispatcherUnhandledException(...)
{
    Log.Error(e.Exception, "Unhandled exception");
    MessageBox.Show($"Error: {ex.Message}\n\nCheck logs for details.");
    e.Handled = true;
}
```

## How to Diagnose Future Issues

### 1. Check Logs First
```
%LOCALAPPDATA%\StockfishCompiler\logs\app-YYYY-MM-DD.log
```
Or run `OpenLogs.bat` or click "Open Log Folder" button in app.

### 2. Look for Error Patterns
- **XamlParseException**: Resource loading issue
- **FileNotFoundException**: Missing DLL or file
- **TypeLoadException**: Assembly version mismatch
- **InvalidOperationException**: DI configuration issue

### 3. Test in Debug Mode
```powershell
dotnet run
```
Will show immediate errors in console.

### 4. Build and Run
```powershell
.\BuildRelease.bat
```
Creates clean release build and shows any build errors.

## Deployment Checklist

? Framework-dependent (requires .NET 8 Desktop Runtime on target PC)
? All DLLs included in publish folder
? Resources properly loaded as application-level merged dictionaries
? Comprehensive logging enabled
? Error dialogs show log file location
? In-app log access via button
? README.md with troubleshooting guide
? BuildRelease.bat for easy packaging

## Log Output Example (Successful Start)

```
2025-11-18 11:35:09.512 [INF] Application starting...
2025-11-18 11:35:09.250 [INF] Log file: C:\Users\...\logs\app-2025-11-18.log
2025-11-18 11:35:09.619 [INF] Services configured successfully
2025-11-18 11:35:09.843 [INF] MainViewModel initializing
2025-11-18 11:35:09.844 [INF] Loading available architectures
2025-11-18 11:35:09.849 [INF] Loaded 8 architectures
2025-11-18 11:35:09.849 [INF] MainViewModel initialized
2025-11-18 11:35:09.850 [INF] BuildViewModel initializing
2025-11-18 11:35:09.864 [INF] BuildViewModel initialized
2025-11-18 11:35:09.870 [INF] MainWindow created, showing...
2025-11-18 11:35:09.978 [INF] Application startup complete
```

## Requirements for End Users

**Required**:
- Windows 10/11
- .NET 8 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/8.0
- MSYS2 with MinGW-w64 (for building Stockfish)

**Installation**:
1. Extract ZIP to any folder
2. Run `StockfishCompiler.exe`
3. If errors occur, click "Open Log Folder" button or run `OpenLogs.bat`

## Build Commands

### Development
```powershell
dotnet run
```

### Release Build
```powershell
.\BuildRelease.bat
```

Or manually:
```powershell
dotnet publish -c Release -r win-x64
```

Output: `bin\Release\net8.0-windows\win-x64\publish\`
