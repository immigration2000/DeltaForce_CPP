param([string]$DotNetPath, [switch]$SkipIntegration, [switch]$KeepDatabase,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$dotnet = Resolve-ServerDotNet -DotNetPath $DotNetPath
Push-Location -LiteralPath $PSScriptRoot
try {
    & $dotnet build .\DeltaForce.Server.slnx -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & $dotnet test .\tests\DeltaForce.Domain.Tests -c $Configuration --no-build --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Domain checks failed' }
    if (-not $SkipIntegration) {
        $api = Join-Path $PSScriptRoot "src\DeltaForce.Api\bin\$Configuration\net10.0\DeltaForce.Api.dll"
        $checks = Join-Path $PSScriptRoot "tests\DeltaForce.IntegrationChecks\bin\$Configuration\net10.0\DeltaForce.IntegrationChecks.dll"
        $checkArguments = @($checks, $dotnet, $api)
        if ($KeepDatabase) { $checkArguments += '--keep-database' }
        & $dotnet @checkArguments
        if ($LASTEXITCODE -ne 0) { throw 'Live MSSQL/HTTP checks failed' }
    }
}
finally { Pop-Location }
