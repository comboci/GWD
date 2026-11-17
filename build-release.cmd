@echo off
setlocal EnableExtensions
cd /d "%~dp0"

title Generic Web Deployment Manager - Unified Release Build
rem Usage: build-release.cmd [--full]
set "BUILD_MODE=FAST"
if /I "%~1"=="--full" set "BUILD_MODE=FULL"
if not "%~1"=="" if /I not "%~1"=="--full" (echo [ERROR] Unknown option: %~1. Use --full or no argument. & exit /b 2)
set "PROJECT=src\DeploymentManager\DeploymentManager.csproj"
set "SOURCE_ROOT=%CD%"
set "DIST=dist"
set "CACHE_ROOT=%LOCALAPPDATA%\GenericWebDeploymentManager\SelfBuildCache"
set "NUGET_PACKAGES=%CACHE_ROOT%\NuGetPackages"
set "NUGET_HTTP_CACHE_PATH=%CACHE_ROOT%\NuGetHttpCache"

for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[xml]$x=Get-Content '.\Directory.Build.props'; $x.Project.PropertyGroup.DeploymentManagerVersion"`) do set "FULL_VERSION=%%V"
for /f "tokens=1 delims=." %%V in ("%FULL_VERSION%") do set "VERSION=%%V"

for /f "usebackq delims=" %%R in (`powershell -NoProfile -ExecutionPolicy Bypass -File ".\resolve-release-root.ps1" -Create -PathOnly`) do set "MANAGER_ROOT=%%R"
if not defined MANAGER_ROOT (echo [ERROR] Could not resolve Manager release/output folder from Settings. & pause & exit /b 1)

if not exist "%CACHE_ROOT%" mkdir "%CACHE_ROOT%" >nul 2>&1
if not exist "%NUGET_PACKAGES%" mkdir "%NUGET_PACKAGES%" >nul 2>&1
if not exist "%NUGET_HTTP_CACHE_PATH%" mkdir "%NUGET_HTTP_CACHE_PATH%" >nul 2>&1
if not exist "%MANAGER_ROOT%" mkdir "%MANAGER_ROOT%" >nul 2>&1
echo [RELEASE] Resolved Settings output: %MANAGER_ROOT%

rem V60: never build directly from a potentially long incoming/versioned path.
rem Stage the complete source in a controlled short path to avoid legacy MAX_PATH failures.
rem Isolate each invocation: a fixed V%VERSION% path can delete another running build.
for /f "usebackq delims=" %%G in (`powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')"`) do set "BUILD_RUN_ID=%%G"
if not defined BUILD_RUN_ID (echo [ERROR] Could not allocate a unique build workspace. & pause & exit /b 1)
set "BUILD_STAGE=%LOCALAPPDATA%\GWDM\Build\V%VERSION%-%BUILD_RUN_ID%"
mkdir "%BUILD_STAGE%" >nul 2>&1 || (echo [ERROR] Could not create isolated build staging folder: %BUILD_STAGE% & pause & exit /b 1)
robocopy "%SOURCE_ROOT%" "%BUILD_STAGE%" /E /XD .git bin obj dist /XF *.user *.suo >nul
if errorlevel 8 (echo [ERROR] Could not stage source into short build workspace. & pause & exit /b 1)
set "PROJECT=%BUILD_STAGE%\src\DeploymentManager\DeploymentManager.csproj"
set "DIST=%BUILD_STAGE%\dist"
echo [BUILD] Short staging workspace: %BUILD_STAGE%

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] .NET SDK was not found in PATH.
  pause
  exit /b 1
)

echo ============================================================
echo Generic Web Deployment Manager V%VERSION% - %BUILD_MODE% RELEASE BUILD
echo Release output: %MANAGER_ROOT%
echo ============================================================
rem The isolated stage excludes obj, so a restore is mandatory on every invocation.
rem Full mode additionally performs an explicit clean and forces dependency resolution.
if /I "%BUILD_MODE%"=="FULL" (
  dotnet clean "%PROJECT%" -c Release --verbosity minimal
  if errorlevel 1 goto :clean_failed
  dotnet restore "%PROJECT%" -r win-x64 --packages "%NUGET_PACKAGES%" --force -p:NuGetAudit=false --verbosity minimal
) else (
  dotnet restore "%PROJECT%" -r win-x64 --packages "%NUGET_PACKAGES%" -p:NuGetAudit=false --verbosity minimal
)
if errorlevel 1 goto :restore_failed

if exist "%DIST%" rmdir /s /q "%DIST%"
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained false --no-restore -p:NuGetAudit=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%DIST%" --verbosity minimal
if errorlevel 1 ( echo [ERROR] Publish failed. Previous EXE remains untouched. & pause & exit /b 1 )

set "BUILT_EXE=%DIST%\GenericWeb.DeploymentManager.exe"
copy /y "%BUILT_EXE%" "%MANAGER_ROOT%\DeploymentManager-V%VERSION%.exe" >nul || (echo [ERROR] Versioned EXE copy failed. & pause & exit /b 1)
copy /y "%BUILT_EXE%" "%MANAGER_ROOT%\DeploymentManager.exe" >nul || (echo [ERROR] Latest EXE copy failed. & pause & exit /b 1)
rem The published executable is outside the staging workspace; remove temporary source after success.
rmdir /s /q "%BUILD_STAGE%" >nul 2>&1
if exist "%BUILD_STAGE%" echo [WARNING] Temporary build workspace could not be removed: %BUILD_STAGE%
start "" "%MANAGER_ROOT%\DeploymentManager-V%VERSION%.exe"
echo Built and published: %MANAGER_ROOT%\DeploymentManager-V%VERSION%.exe
echo Published: %MANAGER_ROOT%
exit /b 0

:clean_failed
echo [ERROR] Clean failed. Previous EXE remains untouched.
exit /b 1

:restore_failed
echo [ERROR] Restore failed. Previous EXE remains untouched.
exit /b 1
