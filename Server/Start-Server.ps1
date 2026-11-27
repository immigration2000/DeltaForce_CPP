param(
    [string]$DotNetPath,
    [string]$Urls = 'http://127.0.0.1:5099',
    [switch]$SeedDemoAccounts,
    [switch]$Background,
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
    if ($Background) {
        $artifactDirectory = Join-Path $PSScriptRoot 'artifacts'
        New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
        $pidFile = Join-Path $artifactDirectory 'server-process.json'
        if (Test-Path -LiteralPath $pidFile) {
            $previous = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
            if (Get-Process -Id $previous.processId -ErrorAction SilentlyContinue) {
                throw 'A managed background server is already running. Use Stop-Server.ps1 first.'
            }
        }
        & $dotnet build (Join-Path $PSScriptRoot 'src\DeltaForce.Api') --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Server build failed' }
        $apiDll = Join-Path $PSScriptRoot 'src\DeltaForce.Api\bin\Debug\net10.0\DeltaForce.Api.dll'
        $serverArguments = @(('"{0}"' -f $apiDll), '--urls', $Urls)
        $apiServerProcess = Start-Process -FilePath $dotnet -ArgumentList $serverArguments -WorkingDirectory (Split-Path -Parent $apiDll) `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $artifactDirectory 'server.stdout.log') `
            -RedirectStandardError (Join-Path $artifactDirectory 'server.stderr.log')
        $readyUrl = $Urls.Split(';')[0].Replace('0.0.0.0','127.0.0.1').Replace('*','127.0.0.1') + '/health/ready'
        $ready = $false
        for ($attempt = 0; $attempt -lt 80; $attempt++) {
            if ($apiServerProcess.HasExited) { break }
            try {
                if ((Invoke-RestMethod -Uri $readyUrl -TimeoutSec 2).status -eq 'Ready') { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        if (-not $ready) {
            if (-not $apiServerProcess.HasExited) { Stop-Process -Id $apiServerProcess.Id }
            throw 'Server did not become ready; inspect Server/artifacts/server.stderr.log and server.stdout.log.'
        }
        @{ processId=$apiServerProcess.Id; startedAt=$apiServerProcess.StartTime.ToUniversalTime().ToString('o');
            startedAtUtcTicks=$apiServerProcess.StartTime.ToUniversalTime().Ticks.ToString();
            dotnetPath=$dotnet; apiPath=$apiDll; urls=$Urls } | ConvertTo-Json | Set-Content -LiteralPath $pidFile -Encoding UTF8
        Write-Output "Development API ready at $Urls (PID $($apiServerProcess.Id)). Use Server/Stop-Server.ps1 to stop."
        return
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
