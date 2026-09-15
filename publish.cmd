@echo off
setlocal

rem ---------------------------------------------------------------------------
rem Builds, tests and publishes docfix — the copies you actually run and share.
rem
rem `dotnet run` compiles from src\ and is NOT what bin\ holds, so editing the
rem source and running the app are two different things. Run this after any
rem change, or you will be using yesterday's build without being told.
rem
rem   bin\docfix            needs the .NET 8 runtime installed
rem   bin\docfix-portable   one self-contained exe — the copy to send to someone
rem
rem Both are published every time. A shareable build that quietly lags behind
rem the one you tested is the same trap as not publishing at all.
rem
rem   publish.cmd            build, test, then publish both
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
    echo TESTS FAILED — nothing was published. bin\ still holds the last good build.
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
echo Building the portable copy. This one takes a minute.
echo.

dotnet publish src\MocdDocFix -c Release -o bin\docfix-portable --nologo ^
    -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 (
    echo.
    echo PORTABLE PUBLISH FAILED — bin\docfix-portable still holds the last good build.
    echo bin\docfix is up to date.
    exit /b 1
)

echo.
echo Published to %CD%\bin
echo   run here      bin\docfix\docfix.exe
echo   send this one bin\docfix-portable\docfix.exe   (no .NET needed)
echo.
echo Neither folder holds anyone's credentials. Each user is asked for their
echo own the first time they run it.
echo.

endlocal
