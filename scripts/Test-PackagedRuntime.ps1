#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This packaged-runtime regression runner requires Windows.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new, empty output path for each test suite.' }
$fullProject = (Resolve-Path -LiteralPath $Project).Path
$name = [IO.Path]::GetFileNameWithoutExtension($fullProject)
dotnet publish $fullProject -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -p:PublishSingleFile=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Test publish failed: $LASTEXITCODE" }
$config = Get-Content -LiteralPath (Join-Path $OutputDirectory "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.includedFrameworks)
if (-not ($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -eq '10.0.13' })) { throw 'Expected self-contained Microsoft.NETCore.App 10.0.13 was not included.' }
foreach ($native in @('hostfxr.dll','hostpolicy.dll','coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $native))) { throw "Missing self-contained runtime file: $native" }
}
Write-Host "Running $name with the published self-contained runtime 10.0.13."
& (Join-Path $OutputDirectory "$name.exe")
if ($LASTEXITCODE -ne 0) { throw "Regression suite failed: $LASTEXITCODE" }
