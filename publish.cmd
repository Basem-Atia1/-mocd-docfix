@echo off
setlocal

rem ---------------------------------------------------------------------------
rem Builds, tests and publishes docfix to bin\docfix — the copy you actually run.
rem
rem `dotnet run` compiles from src\ and is NOT what bin\docfix holds, so editing
rem the source and running the app are two different things. Run this after any
rem change, or you will be using yesterday's build without being told.
rem
rem   publish.cmd            build, test, then publish
rem   publish.cmd --no-test  publish without running the tests (be sure)
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

if /i "%~1"=="--no-test" goto :publish

echo.
echo Running the tests first. Nothing is published if they fail.
echo.

dotnet test tests\MocdDocFix.Tests -c Release --nologo
if errorlevel 1 (
    echo.
    echo TESTS FAILED — nothing was published. bin\docfix still holds the last good build.
    exit /b 1
)

:publish
echo.
dotnet publish src\MocdDocFix -c Release -o bin\docfix --nologo
if errorlevel 1 (
    echo.
    echo PUBLISH FAILED — bin\docfix still holds the last good build.
    exit /b 1
)

echo.
echo Published to %CD%\bin\docfix
echo Run it with:  bin\docfix\docfix.exe
echo.

endlocal
