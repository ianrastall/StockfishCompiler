@echo off
echo Opening StockfishCompiler logs directory...
set LOGDIR=%LOCALAPPDATA%\StockfishCompiler\logs
if not exist "%LOGDIR%" (
    echo No logs directory found yet. Run the application first.
    pause
    exit /b
)
explorer "%LOGDIR%"
