param(
    [Parameter(Mandatory=$true)][string]$ProjectFile,
    [Parameter(Mandatory=$true)][string]$Version
)

$ErrorActionPreference = 'Stop'
[xml]$xml = Get-Content -Path $ProjectFile -Raw
$ns = $xml.DocumentElement.NamespaceURI
$propertyGroup = $xml.Project.PropertyGroup | Select-Object -First 1
if ($null -eq $propertyGroup) {
    $propertyGroup = $xml.CreateElement('PropertyGroup', $ns)
    [void]$xml.Project.AppendChild($propertyGroup)
}

function Set-ElementValue([string]$Name, [string]$Value) {
    $node = $propertyGroup.SelectSingleNode($Name)
    if ($null -eq $node) {
        $node = $xml.CreateElement($Name, $ns)
        [void]$propertyGroup.AppendChild($node)
    }
    $node.InnerText = $Value
}

Set-ElementValue -Name 'Version' -Value $Version
Set-ElementValue -Name 'AssemblyVersion' -Value ($Version + '.0')
Set-ElementValue -Name 'FileVersion' -Value ($Version + '.0')
$xml.Save($ProjectFile)
