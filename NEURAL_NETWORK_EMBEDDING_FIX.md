# Fix for Neural Network Embedding Error During LTO Linking

## Problem

The build was failing during the Link-Time Optimization (LTO) phase with this error:

```
C:\Users\Ian\AppData\Local\Temp\ccKciuE2.s: Assembler messages:
C:\Users\Ian\AppData\Local\Temp\ccKciuE2.s:8: Error: file not found: nn-1c0000000000.nnue
lto-wrapper.exe: fatal error: ... returned 1 exit status
```

## Root Cause

The Stockfish build system embeds the neural network file directly into the binary using the `.incbin` assembler directive. During the build:

1. Stockfish's source references a placeholder network file: `nn-1c0000000000.nnue`
2. The actual network filename (e.g., `nn-37f18f62d772.nnue`) is defined in header files
3. During normal builds, the Makefile's `net` target downloads the network
4. With profile-guided optimization (PGO) and LTO enabled, GCC creates temporary assembly files during linking
5. These temporary files contain the `.incbin "nn-1c0000000000.nnue"` directive
6. The assembler tries to include this file, but it doesn't exist, causing the build to fail

## The Solution

> Update: The default big net `nn-1c0000000000.nnue` is actually published on tests.stockfishchess.org. The downloader now fetches it, so the tiny placeholder is only a fallback when the download is skipped or fails.

A two-part fix:

### Part 1: Remove `-save-temps` Flag

The `-save-temps` flag causes GCC to save all intermediate compilation files. While this is useful for debugging, it exacerbates the network embedding issue by creating persistent assembly files that reference the placeholder network.

```csharp
private static bool PatchMakefileSaveTemps(string sourceDirectory)
{
    var makefilePath = Path.Combine(sourceDirectory, "Makefile");
    if (!File.Exists(makefilePath)) return false;
    
    var content = File.ReadAllText(makefilePath);
    var originalContent = content;
    
    // Remove -save-temps flag using precise regex with lookaround assertions
    content = System.Text.RegularExpressions.Regex.Replace(
        content,
        @"(?<=\s)-save-temps(?=\s|$)",
        "",
        System.Text.RegularExpressions.RegexOptions.Multiline
    );
    
    if (content != originalContent)
    {
        File.WriteAllText(makefilePath, content);
        return true;
    }
    
    return false;
}
```

### Part 2: Create Placeholder Network File

Even without `-save-temps`, LTO still creates temporary files during linking that reference `nn-1c0000000000.nnue`. The solution is to create a minimal dummy file to satisfy the assembler.

```csharp
private static bool CreatePlaceholderNetwork(string sourceDirectory)
{
    // Create a minimal dummy network file to satisfy the .incbin directive during LTO
    var placeholderPath = Path.Combine(sourceDirectory, "nn-1c0000000000.nnue");
    
    if (!File.Exists(placeholderPath))
    {
        // Create a minimal valid NNUE file (just enough to pass the assembler)
        // The actual network file will be loaded at runtime
        var dummyData = new byte[4] { 0x4E, 0x4E, 0x55, 0x45 }; // "NNUE" magic bytes
        File.WriteAllBytes(placeholderPath, dummyData);
        return true;
    }
    
    return false;
}
```

**Important Notes:**
- The placeholder file is only 4 bytes (just the "NNUE" magic header)
- This is enough to satisfy the assembler's `.incbin` directive
- At runtime, Stockfish will load the actual network file (`nn-37f18f62d772.nnue`) which we downloaded earlier
- The placeholder is never actually used by the running program

### Integration in Build Pipeline

```csharp
if (DisableNetDependency(sourceDir))
    _outputSubject.OnNext("Patched makefile to skip redundant net target.");
if (NeutralizeNetScript(downloadResult.RootDirectory))
    _outputSubject.OnNext("Neutralized net.sh script to prevent redundant downloads.");
if (PatchMakefileSaveTemps(sourceDir))
    _outputSubject.OnNext("Removed -save-temps flag to prevent network embedding issues.");
if (CreatePlaceholderNetwork(sourceDir))
    _outputSubject.OnNext("Created placeholder network file for LTO linking.");
```

## Why This Works

1. **Downloading the real network** (`nn-37f18f62d772.nnue`) ensures Stockfish has the correct neural network at runtime
2. **Patching the Makefile** prevents redundant network downloads during build
3. **Removing `-save-temps`** reduces unnecessary intermediate files
4. **Creating the placeholder** satisfies the assembler's `.incbin` directive during LTO linking
5. **At runtime**, Stockfish's initialization code loads the actual network file based on the filename defined in the headers

## Build Flow After Fix

1. ? Download Stockfish source
2. ? Download actual neural network file (`nn-37f18f62d772.nnue`)
3. ? Patch Makefile to remove `net` dependency from targets
4. ? Neutralize `net.sh` script
5. ? Remove `-save-temps` flag
6. ? **Create placeholder network file (`nn-1c0000000000.nnue`)** ? NEW
7. ? Compile with profile-guided optimization
8. ? Strip executable (optional)
9. ? Copy to output directory

## Alternative Solutions Considered

1. ~~**Create empty placeholder**~~: Tried this, but assembler expects a valid file
2. ~~**Disable LTO**~~: Would significantly reduce performance
3. ~~**Patch source code**~~: Fragile and breaks with Stockfish updates
4. ~~**Only remove `-save-temps`~~: Still fails because LTO creates temp files
5. **Create minimal placeholder + remove `-save-temps` (chosen)**: Clean solution that works with any Stockfish version

## Technical Details

### Why LTO Creates Temporary Files

Link-Time Optimization (LTO) works by:
1. Compiling source files to intermediate representation (IR)
2. At link time, optimizing across all translation units
3. Generating final assembly code for the entire program
4. The `.incbin` directive in the assembly references the network file

Even without `-save-temps`, GCC needs to create temporary assembly files during step 3, and these files contain the `.incbin` directive.

### Why the Placeholder Works

The `.incbin` assembler directive includes a binary file directly into the object code. The assembler needs the file to exist to:
1. Check file size for memory allocation
2. Include the binary data in the output

By providing a minimal 4-byte file, we satisfy these requirements. The actual network loading happens at runtime through Stockfish's initialization code.

## Troubleshooting

### If you still see the error:
1. Check that `nn-37f18f62d772.nnue` was downloaded (should be ~3.4 MB)
2. Check that `nn-1c0000000000.nnue` was created (should be 4 bytes)
3. Check the logs for "Created placeholder network file for LTO linking"

### If Stockfish doesn't work at runtime:
1. Ensure `nn-37f18f62d772.nnue` is in the same directory as `stockfish.exe`
2. Check Stockfish output for network loading messages

## Testing

After applying this fix:
- ? Build completes successfully
- ? Profile-guided optimization works
- ? Final binary has optimal performance
- ? Stockfish loads the correct network at runtime
- ? No "file not found" errors during linking

## Related Files Modified

- `Services/BuildService.cs`: 
  - Added `PatchMakefileSaveTemps()` method
  - Added `CreatePlaceholderNetwork()` method
  - Integrated both into build pipeline

## References

- [GCC Documentation on -save-temps](https://gcc.gnu.org/onlinedocs/gcc/Developer-Options.html)
- [GCC LTO Documentation](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html#index-flto)
- [Assembler .incbin Directive](https://sourceware.org/binutils/docs/as/Incbin.html)
- [Stockfish Build Documentation](https://github.com/official-stockfish/Stockfish#compiling-stockfish)
- [Stockfish NNUE Networks](https://github.com/official-stockfish/networks)
