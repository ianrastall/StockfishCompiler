#!/usr/bin/env python3
"""
Universal Stockfish Compiler
A standalone tool for detecting architecture and compiling Stockfish
Compile to EXE with: pyinstaller --onefile --name StockfishCompiler stockfish_compiler.py
"""

import os
import sys
import subprocess
import platform
import shutil
import tempfile
import urllib.request
import json
import zipfile
import tarfile
from pathlib import Path
from typing import Optional, Tuple, Dict, List

class StockfishCompiler:
    def __init__(self):
        self.system = platform.system()
        self.machine = platform.machine().lower()
        self.temp_dir = None
        self.source_dir = None
        self.compiler_type = None
        self.compiler_path = None
        self.arch_detected = None
        self.comp_command = None
        
    def clear_screen(self):
        """Clear the console screen"""
        os.system('cls' if self.system == 'Windows' else 'clear')
    
    def find_make_executable(self) -> str:
        """Find the make executable in MSYS2 installation"""
        if not self.compiler_path or self.system != 'Windows':
            return 'make'
        
        # Try different locations for make in MSYS2
        compiler_path = Path(self.compiler_path)
        
        # First try in the compiler bin directory
        make_exe = compiler_path / 'make.exe'
        if make_exe.exists():
            return str(make_exe)
        
        # Try mingw32-make in compiler bin directory
        mingw_make = compiler_path / 'mingw32-make.exe'
        if mingw_make.exists():
            return str(mingw_make)
        
        # Try MSYS2 usr/bin directory (most common location)
        msys2_root = compiler_path.parent.parent  # Go up from mingw64/bin to msys2 root
        msys_make = msys2_root / 'usr' / 'bin' / 'make.exe'
        if msys_make.exists():
            return str(msys_make)
        
        # Fallback to system PATH
        return 'make'
    
    def print_header(self, text: str):
        """Print a formatted header"""
        print("\n" + "=" * 70)
        print(f"  {text}")
        print("=" * 70 + "\n")
    
    def print_section(self, text: str):
        """Print a section divider"""
        print("\n" + "-" * 70)
        print(f"  {text}")
        print("-" * 70)
    
    def find_msys2_installations(self) -> List[Path]:
        """Find MSYS2 installations on Windows"""
        possible_locations = []
        
        # Common installation paths
        drives = ['C:', 'D:', 'E:', 'F:']
        msys_names = ['msys64', 'msys2']
        
        for drive in drives:
            for msys_name in msys_names:
                path = Path(drive) / msys_name
                if path.exists():
                    possible_locations.append(path)
        
        # Also check user home directory
        home = Path.home()
        for msys_name in msys_names:
            path = home / msys_name
            if path.exists():
                possible_locations.append(path)
        
        return possible_locations
    
    def check_compiler_in_path(self, compiler_path: Path, compiler_name: str) -> Optional[Tuple[str, str, str, str]]:
        """Check if a compiler exists and get its version"""
        exe_name = f"{compiler_name}++"
        if self.system == 'Windows':
            exe_name += '.exe'
        
        compiler_exe = compiler_path / exe_name
        
        if compiler_exe.exists():
            try:
                result = subprocess.run([str(compiler_exe), '--version'], 
                                      capture_output=True, text=True, timeout=5)
                if result.returncode == 0:
                    version = result.stdout.split('\n')[0]
                    comp_type = 'gcc' if compiler_name == 'g' else 'clang'
                    display_name = 'MinGW GCC' if comp_type == 'gcc' else 'Clang/LLVM'
                    return (comp_type, display_name, version, str(compiler_path))
            except Exception:
                pass
        
        return None
    
    def detect_compiler(self) -> Optional[str]:
        """Detect available compiler on the system"""
        self.print_header("Detecting Compiler")
        
        compilers = []
        
        if self.system == 'Windows':
            # First check if compilers are already in PATH
            if shutil.which('g++'):
                try:
                    result = subprocess.run(['g++', '--version'], 
                                          capture_output=True, text=True, timeout=5)
                    if result.returncode == 0:
                        version = result.stdout.split('\n')[0]
                        compilers.append(('gcc', 'MinGW GCC', version, None))
                        print(f"✓ Found in PATH: {version}")
                except Exception:
                    pass
            
            if shutil.which('clang++'):
                try:
                    result = subprocess.run(['clang++', '--version'], 
                                          capture_output=True, text=True, timeout=5)
                    if result.returncode == 0:
                        version = result.stdout.split('\n')[0]
                        compilers.append(('clang', 'Clang/LLVM', version, None))
                        print(f"✓ Found in PATH: {version}")
                except Exception:
                    pass
            
            # Search for MSYS2 installations
            print("\nSearching for MSYS2 installations...")
            msys2_installs = self.find_msys2_installations()
            
            for msys2_path in msys2_installs:
                print(f"  Checking: {msys2_path}")
                
                # Check MinGW64
                mingw64_bin = msys2_path / 'mingw64' / 'bin'
                if mingw64_bin.exists():
                    comp_info = self.check_compiler_in_path(mingw64_bin, 'g')
                    if comp_info and not any(c[3] == comp_info[3] for c in compilers):
                        compilers.append(comp_info)
                        print(f"    ✓ Found MinGW64 GCC: {comp_info[2]}")
                
                # Check UCRT64
                ucrt64_bin = msys2_path / 'ucrt64' / 'bin'
                if ucrt64_bin.exists():
                    comp_info = self.check_compiler_in_path(ucrt64_bin, 'g')
                    if comp_info and not any(c[3] == comp_info[3] for c in compilers):
                        compilers.append(comp_info)
                        print(f"    ✓ Found UCRT64 GCC: {comp_info[2]}")
                
                # Check Clang64
                clang64_bin = msys2_path / 'clang64' / 'bin'
                if clang64_bin.exists():
                    comp_info = self.check_compiler_in_path(clang64_bin, 'clang')
                    if comp_info and not any(c[3] == comp_info[3] for c in compilers):
                        compilers.append(comp_info)
                        print(f"    ✓ Found Clang64: {comp_info[2]}")
        
        else:  # Linux/macOS
            # Check for GCC
            if shutil.which('g++'):
                try:
                    result = subprocess.run(['g++', '--version'], 
                                          capture_output=True, text=True, timeout=5)
                    if result.returncode == 0:
                        version = result.stdout.split('\n')[0]
                        compilers.append(('gcc', 'GCC', version, None))
                        print(f"✓ Found: {version}")
                except Exception:
                    pass
            
            # Check for Clang
            if shutil.which('clang++'):
                try:
                    result = subprocess.run(['clang++', '--version'], 
                                          capture_output=True, text=True, timeout=5)
                    if result.returncode == 0:
                        version = result.stdout.split('\n')[0]
                        compilers.append(('clang', 'Clang', version, None))
                        print(f"✓ Found: {version}")
                except Exception:
                    pass
        
        if not compilers:
            print("\n✗ No suitable C++ compiler found automatically.")
            
            if self.system == 'Windows':
                print("\nWould you like to manually specify the MSYS2 location?")
                choice = input("Enter 'y' to specify path, or 'n' to exit: ").strip().lower()
                
                if choice == 'y':
                    manual_path = input("\nEnter MSYS2 installation path (e.g., D:\\msys2): ").strip()
                    msys2_path = Path(manual_path)
                    
                    if not msys2_path.exists():
                        print(f"✗ Path not found: {msys2_path}")
                        return None
                    
                    # Check for compilers in manual path
                    print(f"\nSearching in: {msys2_path}")
                    
                    for subdir in ['mingw64', 'ucrt64', 'clang64']:
                        bin_path = msys2_path / subdir / 'bin'
                        if bin_path.exists():
                            # Try GCC
                            if subdir != 'clang64':
                                comp_info = self.check_compiler_in_path(bin_path, 'g')
                                if comp_info:
                                    compilers.append(comp_info)
                                    print(f"  ✓ Found in {subdir}: {comp_info[2]}")
                            # Try Clang
                            else:
                                comp_info = self.check_compiler_in_path(bin_path, 'clang')
                                if comp_info:
                                    compilers.append(comp_info)
                                    print(f"  ✓ Found in {subdir}: {comp_info[2]}")
                    
                    if not compilers:
                        print("\n✗ No compilers found in specified path.")
                        return None
                else:
                    return None
            else:
                print("\nPlease install one of the following:")
                print("  - GCC (g++)")
                print("  - Clang (clang++)")
                return None
        
        if len(compilers) == 1:
            self.compiler_type = compilers[0][0]
            self.compiler_path = compilers[0][3]
            self.comp_command = 'mingw' if self.system == 'Windows' and self.compiler_type == 'gcc' else self.compiler_type
            print(f"\nUsing: {compilers[0][1]}")
            if self.compiler_path:
                print(f"Location: {self.compiler_path}")
            return self.compiler_type
        
        # Multiple compilers available - let user choose
        print("\nMultiple compilers detected:")
        for i, (comp_type, comp_name, version, path) in enumerate(compilers, 1):
            print(f"  {i}. {comp_name}")
            print(f"     {version}")
            if path:
                print(f"     Location: {path}")
        
        while True:
            try:
                choice = input(f"\nSelect compiler (1-{len(compilers)}): ").strip()
                idx = int(choice) - 1
                if 0 <= idx < len(compilers):
                    self.compiler_type = compilers[idx][0]
                    self.compiler_path = compilers[idx][3]
                    self.comp_command = 'mingw' if self.system == 'Windows' and self.compiler_type == 'gcc' else self.compiler_type
                    print(f"\nSelected: {compilers[idx][1]}")
                    if self.compiler_path:
                        print(f"Location: {self.compiler_path}")
                    return self.compiler_type
            except (ValueError, IndexError):
                print("Invalid selection. Please try again.")
    
    def detect_architecture(self) -> str:
        """Detect optimal CPU architecture for compilation"""
        self.print_header("Detecting CPU Architecture")
        
        print(f"System: {self.system}")
        print(f"Machine: {self.machine}")
        
        # Handle ARM architectures
        if 'arm' in self.machine or 'aarch64' in self.machine:
            if self.system == 'Darwin':
                print("\nDetected: Apple Silicon")
                self.arch_detected = 'apple-silicon'
                return 'apple-silicon'
            elif 'aarch64' in self.machine or 'arm64' in self.machine:
                print("\nDetected: ARMv8 64-bit")
                self.arch_detected = 'armv8'
                return 'armv8'
            else:
                print("\nDetected: ARMv7 32-bit")
                self.arch_detected = 'armv7'
                return 'armv7'
        
        # x86/x64 architecture detection
        arch = 'x86-64'
        
        try:
            # Determine the correct compiler executable to use
            compiler_exe = None
            if self.compiler_type == 'gcc':
                if self.compiler_path:
                    # Use full path to compiler
                    compiler_exe = str(Path(self.compiler_path) / ('g++.exe' if self.system == 'Windows' else 'g++'))
                else:
                    # Fallback to PATH
                    compiler_exe = 'g++'
            elif self.compiler_type == 'clang':
                if self.compiler_path:
                    # Use full path to compiler
                    compiler_exe = str(Path(self.compiler_path) / ('clang++.exe' if self.system == 'Windows' else 'clang++'))
                else:
                    # Fallback to PATH
                    compiler_exe = 'clang++'
            
            if not compiler_exe:
                raise Exception("No compiler executable determined")
            
            if self.compiler_type == 'gcc':
                # Use GCC to detect features
                result = subprocess.run(
                    [compiler_exe, '-Q', '-march=native', '--help=target'],
                    capture_output=True, text=True, timeout=10
                )
                
                if result.returncode == 0:
                    output = result.stdout.lower()
                    
                    # Check for CPU architecture
                    arch_line = [line for line in output.split('\n') if 'march' in line and 'znver' in line]
                    is_zen = bool(arch_line)
                    
                    # Detect features
                    has_bmi2 = 'mbmi2' in output and '[enabled]' in output
                    has_avx2 = 'mavx2' in output and '[enabled]' in output
                    has_avx512f = 'mavx512f' in output and '[enabled]' in output
                    has_avx512vnni = 'mavx512vnni' in output and '[enabled]' in output
                    has_popcnt = 'mpopcnt' in output and '[enabled]' in output
                    has_sse41 = 'msse4.1' in output and '[enabled]' in output
                    
                    print("\nCPU Features Detected:")
                    if has_avx512vnni:
                        print("  ✓ AVX-512 VNNI")
                        arch = 'x86-64-vnni256'
                    elif has_avx512f:
                        print("  ✓ AVX-512")
                        arch = 'x86-64-avx512'
                    elif has_bmi2 and not is_zen:
                        print("  ✓ BMI2")
                        arch = 'x86-64-bmi2'
                    elif has_avx2:
                        print("  ✓ AVX2")
                        arch = 'x86-64-avx2'
                    elif has_sse41 and has_popcnt:
                        print("  ✓ SSE4.1 + POPCNT")
                        arch = 'x86-64-sse41-popcnt'
                    
            elif self.compiler_type == 'clang':
                # Use Clang to detect features
                result = subprocess.run(
                    [compiler_exe, '-E', '-', '-march=native', '-###'],
                    capture_output=True, text=True, timeout=10,
                    input=""
                )
                
                output = result.stderr.lower()
                
                # Extract target features
                has_avx512vnni = '+avx512vnni' in output
                has_avx512f = '+avx512f' in output
                has_avx512bw = '+avx512bw' in output
                has_bmi2 = '+bmi2' in output
                has_avx2 = '+avx2' in output
                has_sse41 = '+sse4.1' in output
                has_popcnt = '+popcnt' in output
                
                # Check for AMD Zen
                is_zen = 'znver1' in output or 'znver2' in output
                
                print("\nCPU Features Detected:")
                if has_avx512vnni and has_avx512f and has_avx512bw:
                    print("  ✓ AVX-512 VNNI")
                    arch = 'x86-64-vnni256'
                elif has_avx512f and has_avx512bw:
                    print("  ✓ AVX-512")
                    arch = 'x86-64-avx512'
                elif has_bmi2 and not is_zen:
                    print("  ✓ BMI2")
                    arch = 'x86-64-bmi2'
                elif has_avx2:
                    print("  ✓ AVX2")
                    arch = 'x86-64-avx2'
                elif has_sse41 and has_popcnt:
                    print("  ✓ SSE4.1 + POPCNT")
                    arch = 'x86-64-sse41-popcnt'
                
        except Exception as e:
            print(f"\nNote: Could not auto-detect CPU features using {self.compiler_type}")
            print(f"Reason: {e}")
            if self.compiler_path:
                print(f"Compiler path used: {self.compiler_path}")
            else:
                print("Using compiler from system PATH")
            print("Using generic x86-64 architecture")
            
            # Provide some guidance for common issues
            if "The system cannot find the file specified" in str(e):
                print("\nTroubleshooting:")
                print("- Ensure the compiler is properly installed")
                if self.system == 'Windows':
                    print("- If using MSYS2, make sure the mingw64/bin directory contains g++.exe")
                    print("- Try running the compiler manually to verify it works")
        
        print(f"\nOptimal Architecture: {arch}")
        self.arch_detected = arch
        return arch
    
    def choose_architecture(self) -> str:
        """Allow user to choose or confirm architecture"""
        self.print_section("Architecture Selection")
        
        auto_arch = self.arch_detected or 'x86-64'
        
        print(f"Auto-detected architecture: {auto_arch}")
        print("\nOptions:")
        print("  1. Use auto-detected architecture (recommended)")
        print("  2. Choose different architecture")
        
        while True:
            choice = input("\nYour choice (1-2): ").strip()
            
            if choice == '1':
                return auto_arch
            elif choice == '2':
                return self.manual_architecture_selection()
            else:
                print("Invalid choice. Please enter 1 or 2.")
    
    def manual_architecture_selection(self) -> str:
        """Manual architecture selection menu"""
        self.clear_screen()
        self.print_header("Manual Architecture Selection")
        
        archs = [
            ('x86-64-vnni512', 'x86 64-bit with VNNI 512-bit (Intel Sapphire Rapids+, AMD Zen 4+)'),
            ('x86-64-vnni256', 'x86 64-bit with VNNI 256-bit (Intel Cascade Lake+)'),
            ('x86-64-avx512', 'x86 64-bit with AVX-512 (Intel Skylake-X+)'),
            ('x86-64-bmi2', 'x86 64-bit with BMI2 (Intel Haswell+, NOT AMD Zen 1/2)'),
            ('x86-64-avx2', 'x86 64-bit with AVX2 (Intel Haswell+, AMD Zen+)'),
            ('x86-64-sse41-popcnt', 'x86 64-bit with SSE4.1 + POPCNT (Intel Nehalem+)'),
            ('x86-64', 'x86 64-bit generic (maximum compatibility)'),
            ('x86-32', 'x86 32-bit generic'),
            ('armv8', 'ARMv8 64-bit'),
            ('armv7', 'ARMv7 32-bit'),
            ('apple-silicon', 'Apple Silicon (M1/M2/M3)'),
        ]
        
        print("Available architectures:\n")
        for i, (arch, desc) in enumerate(archs, 1):
            print(f"  {i:2d}. {arch:20s} - {desc}")
        
        while True:
            try:
                choice = input(f"\nSelect architecture (1-{len(archs)}): ").strip()
                idx = int(choice) - 1
                if 0 <= idx < len(archs):
                    selected = archs[idx][0]
                    print(f"\nSelected: {selected}")
                    return selected
            except (ValueError, IndexError):
                print("Invalid selection. Please try again.")
    
    def choose_source_version(self) -> Tuple[str, Dict]:
        """Choose between latest release or development version"""
        self.print_section("Source Version Selection")
        
        print("Choose Stockfish version to compile:\n")
        print("  1. Latest stable release (recommended for most users)")
        print("  2. Development version (latest features, may be unstable)")
        
        while True:
            choice = input("\nYour choice (1-2): ").strip()
            
            if choice == '1':
                # Get latest release
                release_info = self.get_latest_release()
                if release_info:
                    return ('release', release_info)
                else:
                    print("\nCouldn't fetch release info. Using development version.")
                    return ('dev', {'url': 'https://github.com/official-stockfish/Stockfish/archive/refs/heads/master.zip',
                                   'version': 'master'})
            elif choice == '2':
                return ('dev', {'url': 'https://github.com/official-stockfish/Stockfish/archive/refs/heads/master.zip',
                               'version': 'master'})
            else:
                print("Invalid choice. Please enter 1 or 2.")
    
    def get_latest_release(self) -> Optional[Dict]:
        """Fetch latest Stockfish release info from GitHub"""
        try:
            print("\nFetching latest release information...")
            url = 'https://api.github.com/repos/official-stockfish/Stockfish/releases/latest'
            req = urllib.request.Request(url, headers={'User-Agent': 'Stockfish-Compiler'})
            
            with urllib.request.urlopen(req, timeout=10) as response:
                data = json.loads(response.read().decode())
                
                tag = data.get('tag_name', 'unknown')
                zipball_url = data.get('zipball_url')
                
                if zipball_url:
                    print(f"Latest release: {tag}")
                    return {
                        'url': zipball_url,
                        'version': tag,
                        'name': data.get('name', tag)
                    }
        except Exception as e:
            print(f"Error fetching release info: {e}")
        
        return None
    
    def download_source(self, source_info: Dict) -> Optional[Path]:
        """Download and extract Stockfish source code"""
        self.print_header(f"Downloading Stockfish {source_info['version']}")
        
        self.temp_dir = Path(tempfile.mkdtemp(prefix='stockfish_build_'))
        print(f"Build directory: {self.temp_dir}")
        
        try:
            # Download
            zip_path = self.temp_dir / 'stockfish.zip'
            print(f"\nDownloading from: {source_info['url']}")
            print("Please wait...")
            
            urllib.request.urlretrieve(source_info['url'], zip_path)
            print("✓ Download complete")
            
            # Extract
            print("\nExtracting source code...")
            with zipfile.ZipFile(zip_path, 'r') as zip_ref:
                zip_ref.extractall(self.temp_dir)
            
            # Find the extracted directory
            extracted_dirs = [d for d in self.temp_dir.iterdir() if d.is_dir()]
            if not extracted_dirs:
                raise Exception("Could not find extracted source directory")
            
            self.source_dir = extracted_dirs[0] / 'src'
            
            if not self.source_dir.exists():
                raise Exception(f"Source directory not found: {self.source_dir}")
            
            print(f"✓ Source extracted to: {self.source_dir}")
            return self.source_dir
            
        except Exception as e:
            print(f"\n✗ Error downloading source: {e}")
            return None
    
    def download_neural_network(self) -> bool:
        """Download the NNUE neural network file"""
        self.print_section("Downloading Neural Network")
        
        try:
            print("Downloading NNUE network file...")
            
            # Set up environment with compiler path if needed
            env = os.environ.copy()
            make_cmd = self.find_make_executable()
            
            if self.compiler_path and self.system == 'Windows':
                env['PATH'] = f"{self.compiler_path}{os.pathsep}{env.get('PATH', '')}"
                # Also add usr/bin to PATH for MSYS2 tools
                compiler_path = Path(self.compiler_path)
                msys2_root = compiler_path.parent.parent
                msys_bin = msys2_root / 'usr' / 'bin'
                if msys_bin.exists():
                    env['PATH'] = f"{msys_bin}{os.pathsep}{env['PATH']}"
            
            print(f"Using make command: {make_cmd}")
            
            # Run make net command
            result = subprocess.run(
                [make_cmd, 'net'],
                cwd=self.source_dir,
                capture_output=True,
                text=True,
                timeout=60,
                env=env
            )
            
            if result.returncode == 0:
                print("✓ Neural network downloaded successfully")
                return True
            else:
                print(f"Warning: Could not download network file")
                print(f"Error: {result.stderr}")
                print("\nThe compilation will continue, but you may need to download")
                print("the network file manually later.")
                
                choice = input("\nContinue anyway? (y/n): ").strip().lower()
                return choice == 'y'
                
        except Exception as e:
            print(f"Error downloading network: {e}")
            if self.system == 'Windows' and "cannot find the file specified" in str(e):
                print("\nTroubleshooting:")
                print("- Make sure 'make' or 'mingw32-make' is available in your MSYS2 installation")
                print("- You can install it with: pacman -S make mingw-w64-x86_64-toolchain")
                if self.compiler_path:
                    print(f"- Checked compiler path: {self.compiler_path}")
            
            choice = input("\nContinue anyway? (y/n): ").strip().lower()
            return choice == 'y'
    
    def compile_stockfish(self, arch: str) -> bool:
        """Compile Stockfish with the specified architecture"""
        self.print_header("Compiling Stockfish")
        
        print(f"Architecture: {arch}")
        print(f"Compiler: {self.comp_command}")
        if self.compiler_path:
            print(f"Compiler Location: {self.compiler_path}")
        print(f"\nThis may take several minutes...")
        print("=" * 70)
        
        try:
            # Set up environment with compiler path if needed
            env = os.environ.copy()
            make_cmd = self.find_make_executable()
            
            if self.compiler_path and self.system == 'Windows':
                # Add compiler bin directory to PATH
                env['PATH'] = f"{self.compiler_path}{os.pathsep}{env.get('PATH', '')}"
                print(f"\nAdded to PATH: {self.compiler_path}")
                
                # Also add usr/bin to PATH for MSYS2 tools
                compiler_path = Path(self.compiler_path)
                msys2_root = compiler_path.parent.parent
                msys_bin = msys2_root / 'usr' / 'bin'
                if msys_bin.exists():
                    env['PATH'] = f"{msys_bin}{os.pathsep}{env['PATH']}"
                    print(f"Added MSYS2 tools to PATH: {msys_bin}")
                
                print(f"Using make: {make_cmd}")
            
            # Determine number of jobs
            import multiprocessing
            jobs = multiprocessing.cpu_count()
            
            # Build command
            make_cmd_args = [make_cmd, f'-j{jobs}', 'profile-build', 
                           f'ARCH={arch}', f'COMP={self.comp_command}']
            
            print(f"\nRunning: {' '.join(make_cmd_args)}\n")
            
            # Run compilation
            process = subprocess.Popen(
                make_cmd_args,
                cwd=self.source_dir,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
                env=env
            )
            
            # Stream output
            if process.stdout:
                for line in process.stdout:
                    print(line, end='')
            
            process.wait()
            
            if process.returncode == 0:
                print("\n" + "=" * 70)
                print("✓ Compilation successful!")
                return True
            else:
                print("\n" + "=" * 70)
                print("✗ Compilation failed!")
                return False
                
        except Exception as e:
            print(f"\n✗ Compilation error: {e}")
            return False
    
    def strip_executable(self) -> bool:
        """Strip debug symbols from executable"""
        self.print_section("Optimizing Executable")
        
        try:
            print("Stripping debug symbols...")
            
            # Set up environment with compiler path if needed
            env = os.environ.copy()
            make_cmd = self.find_make_executable()
            
            if self.compiler_path and self.system == 'Windows':
                env['PATH'] = f"{self.compiler_path}{os.pathsep}{env.get('PATH', '')}"
                
                # Also add usr/bin to PATH for MSYS2 tools
                compiler_path = Path(self.compiler_path)
                msys2_root = compiler_path.parent.parent
                msys_bin = msys2_root / 'usr' / 'bin'
                if msys_bin.exists():
                    env['PATH'] = f"{msys_bin}{os.pathsep}{env['PATH']}"
            
            result = subprocess.run(
                [make_cmd, 'strip', f'COMP={self.comp_command}'],
                cwd=self.source_dir,
                capture_output=True,
                text=True,
                timeout=30,
                env=env
            )
            
            if result.returncode == 0:
                print("✓ Executable optimized")
                return True
            else:
                print("Note: Could not strip executable (not critical)")
                return True
                
        except Exception as e:
            print(f"Note: Strip failed: {e} (not critical)")
            return True
    
    def copy_executable(self, arch: str, version: str) -> Optional[Path]:
        """Copy the compiled executable to a user-friendly location"""
        self.print_section("Copying Executable")
        
        try:
            # Check if source directory exists
            if self.source_dir is None:
                print("✗ Source directory not available")
                return None
            
            # Find the executable
            exe_name = 'stockfish.exe' if self.system == 'Windows' else 'stockfish'
            source_exe = self.source_dir / exe_name
            
            if not source_exe.exists():
                print(f"✗ Could not find executable: {source_exe}")
                return None
            
            # Determine output location
            output_dir = Path.cwd()
            output_name = f"stockfish_{version}_{arch}{'.exe' if self.system == 'Windows' else ''}"
            output_path = output_dir / output_name
            
            # Copy executable
            shutil.copy2(source_exe, output_path)
            
            # Make executable on Unix systems
            if self.system != 'Windows':
                os.chmod(output_path, 0o755)
            
            print(f"✓ Executable saved to: {output_path}")
            print(f"\nFile size: {output_path.stat().st_size / (1024*1024):.1f} MB")
            
            return output_path
            
        except Exception as e:
            print(f"✗ Error copying executable: {e}")
            return None
    
    def cleanup(self):
        """Clean up temporary files"""
        if self.temp_dir and self.temp_dir.exists():
            try:
                shutil.rmtree(self.temp_dir)
                print("\n✓ Temporary files cleaned up")
            except Exception as e:
                print(f"\nNote: Could not clean up temp files: {e}")
                print(f"You may want to manually delete: {self.temp_dir}")
    
    def run(self):
        """Main execution flow"""
        self.clear_screen()
        self.print_header("Universal Stockfish Compiler")
        
        print("This tool will automatically detect your system and compile")
        print("Stockfish chess engine optimized for your CPU.\n")
        
        input("Press Enter to begin...")
        
        # Step 1: Detect compiler
        self.clear_screen()
        if not self.detect_compiler():
            input("\nPress Enter to exit...")
            return 1
        
        input("\nPress Enter to continue...")
        
        # Step 2: Detect architecture
        self.clear_screen()
        self.detect_architecture()
        
        input("\nPress Enter to continue...")
        
        # Step 3: Choose architecture
        self.clear_screen()
        arch = self.choose_architecture()
        
        # Step 4: Choose source version
        self.clear_screen()
        source_type, source_info = self.choose_source_version()
        
        # Step 5: Download source
        self.clear_screen()
        if not self.download_source(source_info):
            input("\nPress Enter to exit...")
            return 1
        
        input("\nPress Enter to continue...")
        
        # Step 6: Download neural network
        self.clear_screen()
        if not self.download_neural_network():
            self.cleanup()
            input("\nPress Enter to exit...")
            return 1
        
        input("\nPress Enter to start compilation...")
        
        # Step 7: Compile
        self.clear_screen()
        if not self.compile_stockfish(arch):
            self.cleanup()
            input("\nPress Enter to exit...")
            return 1
        
        # Step 8: Strip executable
        self.strip_executable()
        
        # Step 9: Copy to output location
        output_path = self.copy_executable(arch, source_info['version'])
        
        # Step 10: Cleanup
        self.cleanup()
        
        # Final message
        self.print_header("Compilation Complete!")
        
        if output_path:
            print("Your optimized Stockfish executable is ready!")
            print(f"\nLocation: {output_path}")
            print(f"Architecture: {arch}")
            print(f"Version: {source_info['version']}")
            
            print("\n" + "=" * 70)
            print("\nTo use Stockfish:")
            print(f"  1. Run: {output_path}")
            print("  2. Or use it with a chess GUI (e.g., Arena, ChessBase)")
            print("\nFor command-line use, type 'uci' then 'quit' to test.")
        
        input("\n\nPress Enter to exit...")
        return 0


def main():
    """Entry point"""
    try:
        compiler = StockfishCompiler()
        return compiler.run()
    except KeyboardInterrupt:
        print("\n\nOperation cancelled by user.")
        return 1
    except Exception as e:
        print(f"\n\nUnexpected error: {e}")
        import traceback
        traceback.print_exc()
        input("\nPress Enter to exit...")
        return 1


if __name__ == '__main__':
    sys.exit(main())