param(
    [string]$DotNetPath,
    [string]$Urls = 'http://127.0.0.1:5099',
    [switch]$SeedDemoAccounts,
    [string]$ConnectionString
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$dotnet = Resolve-ServerDotNet -DotNetPath $DotNetPath
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousSeed = $env:Development__SeedPassword
$previousConnection = $env:ConnectionStrings__Inventory
Push-Location -LiteralPath $PSScriptRoot
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    if ($ConnectionString) { $env:ConnectionStrings__Inventory = $ConnectionString }
    if ($SeedDemoAccounts) {
        $securePassword = Read-Host 'Password for NEW demo/demo2 accounts (12-128 characters)' -AsSecureString
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        try { $seedPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
        if ($seedPassword.Length -lt 12 -or $seedPassword.Length -gt 128) { throw 'Use a password with 12-128 characters.' }
        $env:Development__SeedPassword = $seedPassword
        $seedPassword = $null
    }
    & $dotnet run --project (Join-Path $PSScriptRoot 'src\DeltaForce.Api') --no-launch-profile -- --urls $Urls
    if ($LASTEXITCODE -ne 0) { throw "Server exited with code $LASTEXITCODE" }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:Development__SeedPassword = $previousSeed
    $env:ConnectionStrings__Inventory = $previousConnection
    Pop-Location
}
