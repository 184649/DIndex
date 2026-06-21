param(
    [Parameter(Mandatory = $true)][string]$ProjectFile,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file was not found: $ProjectFile"
}

[xml]$xml = Get-Content -LiteralPath $ProjectFile -Raw
$projectNode = $xml.Project
if ($null -eq $projectNode) {
    throw "Invalid csproj format: Project element was not found."
}

$propertyGroup = @($projectNode.PropertyGroup | Where-Object { $_ -ne $null } | Select-Object -First 1)[0]
if ($null -eq $propertyGroup) {
    $propertyGroup = $xml.CreateElement('PropertyGroup')
    [void]$projectNode.AppendChild($propertyGroup)
}

function Set-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $node = $propertyGroup.SelectSingleNode($Name)
    if ($null -eq $node) {
        $node = $xml.CreateElement($Name)
        [void]$propertyGroup.AppendChild($node)
    }

    $node.InnerText = $Value
}

Set-ProjectProperty -Name 'Version' -Value $Version
Set-ProjectProperty -Name 'AssemblyVersion' -Value ($Version + '.0')
Set-ProjectProperty -Name 'FileVersion' -Value ($Version + '.0')

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($ProjectFile, $false, $utf8NoBom)
try {
    $xml.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Host "[OK] csproj version updated: $Version"
