function Write-MsiComFailure([string]$Operation, $Failure) {
    $exception = $Failure.Exception
    for ($depth = 0; $null -ne $exception -and $depth -lt 4; $depth++) {
        $message = $exception.Message -replace 'S-\d+(?:-\d+)+', '[SID]'
        Write-Host "MSI COM failure operation=$Operation depth=$depth type=$($exception.GetType().FullName) HRESULT=0x$($exception.HResult.ToString('X8')) message=$message"
        $exception = $exception.InnerException
    }
}
function Get-MsiProductIdentity($Product) {
    # These are documented IProduct read-only properties, but PowerShell's COM
    # adapter does not expose them consistently under StrictMode. Invoke their
    # IDispatch property getters explicitly; do not substitute the query inputs.
    # https://learn.microsoft.com/en-us/windows/win32/msi/product-object
    $type = $Product.GetType()
    $getProperty = [Reflection.BindingFlags]::GetProperty
    $noArguments = [object[]]::new(0)
    try {
        [pscustomobject]@{
            productCode = [string]$type.InvokeMember('ProductCode', $getProperty, $null, $Product, $noArguments)
            context = [int]$type.InvokeMember('Context', $getProperty, $null, $Product, $noArguments)
            userSid = [string]$type.InvokeMember('UserSid', $getProperty, $null, $Product, $noArguments)
        }
    } catch { Write-MsiComFailure 'IProduct identity getters' $_; throw }
}
function Assert-MsiProductIdentityApi([string]$ProductCode, [string]$CurrentUserSid) {
    # IProduct can represent an unknown product. Constructing this object is
    # read-only and exercises all identity getters before building any fixtures.
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $product = $null
    try {
        $arguments = [object[]]@($ProductCode, $currentUserSid, 2)
        $product = $installer.GetType().InvokeMember('Product', [Reflection.BindingFlags]::GetProperty, $null, $installer, $arguments)
        $actual = Get-MsiProductIdentity $product
        if ($actual.productCode -ne $ProductCode) { throw 'MSI identity API preflight returned a different ProductCode.' }
        if ($actual.context -ne 2 -or $actual.userSid -ne $CurrentUserSid) { throw 'MSI identity API preflight returned a different context or SID.' }
        Write-Host "MSI identity getter preflight: product=$($actual.productCode) context=$($actual.context) currentUser=$($actual.userSid -eq $currentUserSid)"
    } catch {
        Write-MsiComFailure 'Unknown-product identity preflight' $_
        throw
    } finally {
        if ($null -ne $product) { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($product) }
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}
function Get-MsiProductRegistrations([string]$ProductCode, [string]$CurrentUserSid) {
    # Query MSI's supported context-aware API, not a registry layout assumption.
    # https://learn.microsoft.com/en-us/windows/win32/msi/installer-productsex
    # https://learn.microsoft.com/en-us/windows/win32/msi/product-installproperty
    # Context 2=current-user unmanaged, 1=current-user managed, 4=machine.
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        foreach ($context in @(2, 1, 4)) {
            # COM ProductsEx uses an empty string for machine enumeration, as in
            # Microsoft's WiLstPrdEx.vbs sample. PowerShell's COM binder rejects
            # a null argument here. Explicit current SID restricts user queries.
            $querySid = if ($context -eq 4) { [string]::Empty } else { $currentUserSid }
            $products = $null
            $count = 0
            try {
                $products = $installer.ProductsEx($ProductCode, $querySid, $context)
                foreach ($product in $products) {
                    try {
                        $count++
                        # SDK Msi.h: INSTALLPROPERTY_PRODUCTSTATE is the literal State.
                        $state = [string]$product.InstallProperty('State')
                        $version = if ($state -eq '5') { [string]$product.InstallProperty('VersionString') } else { '<not installed>' }
                        $productIdentity = Get-MsiProductIdentity $product
                        $entry = [pscustomobject]@{
                            productCode = $productIdentity.productCode
                            context = $productIdentity.context
                            userSid = $productIdentity.userSid
                            state = $state
                            version = $version
                        }
                        Write-Host "MSI registration $ProductCode context=$($entry.context) currentUser=$($entry.userSid -eq $currentUserSid) state=$state version=$version"
                        $entry
                    } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($product) }
                }
                if ($count -eq 0) { Write-Host "MSI registration $ProductCode context=$context absent" }
            } catch {
                Write-Host "MSI registration query failed: product=$ProductCode context=$context error=$($_.Exception.Message)"
                throw
            } finally {
                if ($null -ne $products) { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($products) }
            }
        }
    } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
}
