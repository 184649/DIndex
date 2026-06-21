@echo off
setlocal EnableExtensions EnableDelayedExpansion

echo ========================================
echo DIndex GitHub Zip Release Build
echo ========================================

set ROOT_DIR=%~dp0
set PROJECT_DIR=%ROOT_DIR%src\DIndex
set PROJECT_FILE=%PROJECT_DIR%\DIndex.csproj
set UPDATER_DIR=%ROOT_DIR%src\DIndexUpdater
set UPDATER_PROJECT_FILE=%UPDATER_DIR%\DIndexUpdater.csproj
set RELEASE_DIR=%ROOT_DIR%release
set EXE_NAME=DIndex.exe
set UPDATER_EXE_NAME=DIndexUpdater.exe

set REPO_OWNER=Chairman-bits
set REPO_NAME=DIndex
set BRANCH_NAME=main

cd /d "%ROOT_DIR%"

set /p VERSION=Enter version (ex: 1.0.1): 

if "%VERSION%"=="" (
  echo [ERROR] version is required.
  pause
  exit /b 1
)

set APP_ZIP_URL=https://raw.githubusercontent.com/%REPO_OWNER%/%REPO_NAME%/%BRANCH_NAME%/DIndex.zip
set UPDATER_ZIP_URL=https://raw.githubusercontent.com/%REPO_OWNER%/%REPO_NAME%/%BRANCH_NAME%/DIndexUpdater.zip
set NOTES_URL=https://raw.githubusercontent.com/%REPO_OWNER%/%REPO_NAME%/%BRANCH_NAME%/release-notes.json

echo.
echo [INFO] version=%VERSION%
echo [INFO] app zip=%APP_ZIP_URL%
echo [INFO] updater zip=%UPDATER_ZIP_URL%
echo.

echo [1/6] update csproj version...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$files=@('%PROJECT_FILE%','%UPDATER_PROJECT_FILE%');" ^
  "$version='%VERSION%';" ^
  "foreach($p in $files) {" ^
  "  if(-not (Test-Path $p)) { throw ('project file not found: ' + $p) }" ^
  "  $text=Get-Content $p -Raw;" ^
  "  function Set-XmlTag([string]$name,[string]$value) {" ^
  "    $pattern='<' + $name + '>.*?</' + $name + '>';" ^
  "    $replacement='<' + $name + '>' + $value + '</' + $name + '>';" ^
  "    if($script:text -match $pattern) { $script:text=[regex]::Replace($script:text,$pattern,$replacement,1); }" ^
  "    else { $script:text=[regex]::Replace($script:text,'</PropertyGroup>','    ' + $replacement + [Environment]::NewLine + '  </PropertyGroup>',1); }" ^
  "  }" ^
  "  Set-XmlTag 'Version' $version;" ^
  "  Set-XmlTag 'AssemblyVersion' ($version + '.0');" ^
  "  Set-XmlTag 'FileVersion' ($version + '.0');" ^
  "  Set-Content -Path $p -Value $text -Encoding UTF8;" ^
  "}"

if errorlevel 1 (
  echo [ERROR] version update failed.
  pause
  exit /b 1
)

echo [2/6] clean...
if exist "%RELEASE_DIR%" rmdir /s /q "%RELEASE_DIR%"
mkdir "%RELEASE_DIR%"

echo [3/6] publish DIndex...
cd /d "%PROJECT_DIR%"

dotnet publish -c Release -r win-x64 --self-contained true ^
 /p:PublishSingleFile=true ^
 /p:IncludeNativeLibrariesForSelfExtract=true ^
 /p:EnableCompressionInSingleFile=true ^
 /p:PublishTrimmed=false

if errorlevel 1 (
  echo [ERROR] DIndex publish failed.
  pause
  exit /b 1
)

echo [4/6] publish updater...
cd /d "%UPDATER_DIR%"

dotnet publish -c Release -r win-x64 --self-contained true ^
 /p:PublishSingleFile=true ^
 /p:PublishTrimmed=false

if errorlevel 1 (
  echo [ERROR] updater publish failed.
  pause
  exit /b 1
)

echo [5/6] create compressed zip files...
set APP_PUBLISH=%PROJECT_DIR%\bin\Release\net8.0-windows\win-x64\publish
set UPDATER_PUBLISH=%UPDATER_DIR%\bin\Release\net8.0\win-x64\publish

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$release='%RELEASE_DIR%';" ^
  "$app='%APP_PUBLISH%\%EXE_NAME%';" ^
  "$updater='%UPDATER_PUBLISH%\%UPDATER_EXE_NAME%';" ^
  "if(-not (Test-Path $app)) { throw ('app exe not found: ' + $app) }" ^
  "if(-not (Test-Path $updater)) { throw ('updater exe not found: ' + $updater) }" ^
  "Compress-Archive -Path $app -DestinationPath (Join-Path $release 'DIndex.zip') -Force;" ^
  "Compress-Archive -Path $updater -DestinationPath (Join-Path $release 'DIndexUpdater.zip') -Force;"

if errorlevel 1 (
  echo [ERROR] zip creation failed.
  pause
  exit /b 1
)

echo [6/6] generate version.json...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$version='%VERSION%';" ^
  "$app='%APP_ZIP_URL%';" ^
  "$updater='%UPDATER_ZIP_URL%';" ^
  "$notes='%NOTES_URL%';" ^
  "$release='%RELEASE_DIR%';" ^
  "$obj=[ordered]@{ version=$version; downloadUrl=$app; updaterUrl=$updater; releaseNotes=$notes };" ^
  "$json=$obj | ConvertTo-Json -Depth 5;" ^
  "Set-Content -Path (Join-Path $release 'version.json') -Value $json -Encoding UTF8;" ^
  "$notesObj=[ordered]@{ version=$version; notes=@('DIndex v' + $version) };" ^
  "$notesJson=$notesObj | ConvertTo-Json -Depth 5;" ^
  "Set-Content -Path (Join-Path $release 'release-notes.json') -Value $notesJson -Encoding UTF8;"

if errorlevel 1 (
  echo [ERROR] metadata generation failed.
  pause
  exit /b 1
)

echo.
echo ========================================
echo DONE
echo ========================================
echo Put these files on main branch root:
echo   release\DIndex.zip
echo   release\DIndexUpdater.zip
echo   release\version.json
echo   release\release-notes.json
echo.
pause
exit /b 0
