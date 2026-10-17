#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'MSI registration API preflight requires Windows.' }
. (Join-Path $PSScriptRoot 'MsiRegistration.ps1')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $currentUserSid = $identity.User.Value } finally { $identity.Dispose() }
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
# Read-only: no MSI build, installation, registration, or file-system mutation.
Assert-MsiProductIdentityApi $productCode $currentUserSid
$registrations = @(Get-MsiProductRegistrations $productCode $currentUserSid)
if ($registrations.Count -ne 0) { throw 'Fresh MSI preflight ProductCode unexpectedly exists.' }
Write-Host 'PASS MSI COM identity getters and current-user/machine enumeration preflight'
