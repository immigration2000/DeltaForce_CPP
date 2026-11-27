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
$previousReset = $env:Development__ResetDemoPasswords
$previousConnection = $env:ConnectionStrings__Inventory
Push-Location -LiteralPath $PSScriptRoot
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    if ($ConnectionString) { $env:ConnectionStrings__Inventory = $ConnectionString }
    if ($SeedDemoAccounts) {
        $env:Development__SeedPassword = '1234'
        $env:Development__ResetDemoPasswords = 'true'
    }
    & $dotnet run --project (Join-Path $PSScriptRoot 'src\DeltaForce.Api') --no-launch-profile -- --urls $Urls
    if ($LASTEXITCODE -ne 0) { throw "Server exited with code $LASTEXITCODE" }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:Development__SeedPassword = $previousSeed
    $env:Development__ResetDemoPasswords = $previousReset
    $env:ConnectionStrings__Inventory = $previousConnection
    Pop-Location
}
