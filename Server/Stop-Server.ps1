$ErrorActionPreference = 'Stop'
$pidFile = Join-Path $PSScriptRoot 'artifacts\server-process.json'
if (-not (Test-Path -LiteralPath $pidFile)) { Write-Output 'No managed background API.'; return }
$record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
$expectedApi = Join-Path $PSScriptRoot 'src\DeltaForce.Api\bin\Debug\net10.0\DeltaForce.Api.dll'
$apiServerProcess = Get-Process -Id $record.processId -ErrorAction SilentlyContinue
if ($apiServerProcess) {
    if ($record.startedAtUtcTicks) { $recordStart = [DateTime]::new([long]$record.startedAtUtcTicks, [DateTimeKind]::Utc) }
    elseif ($record.startedAt -is [DateTime]) { $recordStart = $record.startedAt.ToUniversalTime() }
    else { $recordStart = [DateTimeOffset]::Parse([string]$record.startedAt).UtcDateTime }
    if ($record.apiPath -ne $expectedApi -or $apiServerProcess.Path -ne $record.dotnetPath -or
        [Math]::Abs(($apiServerProcess.StartTime.ToUniversalTime() - $recordStart).TotalSeconds) -gt 1) {
        throw 'The saved PID no longer identifies the managed API. No process was stopped.'
    }
    Stop-Process -Id $apiServerProcess.Id
    $apiServerProcess.WaitForExit()
}
Remove-Item -LiteralPath $pidFile
Write-Output 'Managed development API stopped.'
