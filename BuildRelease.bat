@echo off
echo =============================================
echo StockfishCompiler - Build Release Package
echo =============================================
echo.

echo Stopping any running instances...
taskkill /IM StockfishCompiler.exe /F >nul 2>&1

echo.
echo Cleaning previous builds...
rmdir /s /q bin obj publish 2>nul

echo.
echo Building Release...
dotnet publish -c Release -r win-x64

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build FAILED!
    pause
    exit /b 1
)

echo.
echo Copying files to release folder...
if not exist "Release" mkdir Release
xcopy /E /I /Y "bin\Release\net8.0-windows\win-x64\publish\*" "Release\"
copy /Y "OpenLogs.bat" "Release\"
copy /Y "README.md" "Release\"

echo.
echo =============================================
echo SUCCESS! Release package created in Release\ folder
echo =============================================
echo.
echo Files:
dir /B Release

echo.
echo Run Release\StockfishCompiler.exe to test
pause
