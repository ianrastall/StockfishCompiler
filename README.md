# StockfishCompiler

A WPF application for compiling Stockfish chess engine with automatic CPU architecture detection.

## Features

- Automatic compiler detection (MSYS2, MinGW, Clang)
- CPU architecture detection for optimal performance
- Download Stockfish source (stable or development)
- Profile-guided optimization build
- Real-time build output and progress
- Dark-themed UI

## Requirements

- Windows 10/11 with .NET 8 Runtime
- MSYS2 with MinGW-w64 or Clang installed

## Installation

### Install MSYS2

1. Download and install MSYS2 from https://www.msys2.org/
2. Open MSYS2 MSYS terminal and run:
   ```bash
   pacman -Syu
   pacman -S mingw-w64-x86_64-gcc make
   ```

### Run StockfishCompiler

1. Extract the release to a folder
2. Run `StockfishCompiler.exe`

## Usage

1. **Compiler Setup Tab**
   - Click "Detect Compilers" to find installed compilers
   - Click "Detect Optimal Architecture" to auto-select best CPU architecture

2. **Build Configuration Tab**
   - Adjust parallel jobs (defaults to your CPU core count)
   - Set output directory where compiled Stockfish will be saved
   - Choose build options (download network, strip executable)

3. **Compilation Tab**
   - Click "Start Build" to begin compilation
   - View real-time build output
   - Cancel build if needed

## Troubleshooting

### Application won't start

Check logs at: `%LOCALAPPDATA%\StockfishCompiler\logs\`

Or run `OpenLogs.bat` (included in release) to open the logs folder.

### No compilers found

Make sure MSYS2 is installed and MinGW-w64 toolchain is installed:
```bash
pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-make
```

### Build fails

- Ensure `make` is available in MSYS2 (`C:\msys64\usr\bin\make.exe`)
- Check that compiler path is correct in Compiler Setup tab
- Review build output in Compilation tab for specific errors

## Logs

Application logs are saved to:
```
%LOCALAPPDATA%\StockfishCompiler\logs\app-YYYY-MM-DD.log
```

Use `OpenLogs.bat` to quickly open the logs folder.

## License

MIT License - see LICENSE file for details

## Credits

- Stockfish: https://github.com/official-stockfish/Stockfish
- UI Framework: WPF (.NET 8)
- MVVM Toolkit: CommunityToolkit.Mvvm
- Logging: Serilog
