@echo off
REM Build the .msi for PrintShop Companion.
REM Requires WiX v5 on PATH: dotnet tool install -g wix
REM Assumes you have already published the app to artifacts\app\

setlocal
set PUBLISH_DIR=%~dp0..\artifacts\app
set REPO_ROOT=%~dp0..
set OUTPUT_DIR=%~dp0build

if not exist "%PUBLISH_DIR%\PrintShopCompanion.exe" (
  echo [BuildInstaller] Publish dir is empty. Run "dotnet publish" first.
  exit /b 1
)

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

echo [BuildInstaller] Compiling PrintShopCompanion.msi ...
wix build "%~dp0PrintShopCompanion.Installer.wxs" ^
  -d PublishDir="%PUBLISH_DIR%\\" ^
  -d RepoRoot="%REPO_ROOT%" ^
  -o "%OUTPUT_DIR%\PrintShopCompanion.msi"

if %ERRORLEVEL% NEQ 0 (
  echo [BuildInstaller] MSI build failed.
  exit /b %ERRORLEVEL%
)

echo [BuildInstaller] Success: %OUTPUT_DIR%\PrintShopCompanion.msi
endlocal
