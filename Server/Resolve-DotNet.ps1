function Resolve-ServerDotNet {
    param([string]$DotNetPath)
    $candidates = @()
    if ($DotNetPath) { $candidates += $DotNetPath }
    else {
        $installed = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($installed) { $candidates += $installed.Source }
        $candidates += 'C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            $versions = & $candidate --list-sdks
            if ($LASTEXITCODE -eq 0 -and ($versions -match '^10\.')) {
                $resolved = (Resolve-Path -LiteralPath $candidate).Path
                $env:DOTNET_ROOT = Split-Path -Parent $resolved
                return $resolved
            }
        }
    }
    throw 'A .NET 10 SDK is required. Install it or pass -DotNetPath to a dotnet.exe with the SDK.'
}
