$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '..')).FullName
Push-Location -LiteralPath $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'chainloot-dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $env:TEMP 'chainloot-nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

    foreach ($image in @('university-chainloot-backend:latest', 'university-chainloot-frontend:latest', 'postgres:16-alpine')) {
        & docker image inspect $image *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Brakuje lokalnego obrazu $image. Skrypt nie pobiera obrazów z internetu."
        }
    }

    & dotnet publish backend/ChainLoot.Backend.csproj --no-restore -c Release -p:IsTransformWebConfigDisabled=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Kompilacja backendu offline nie powiodła się. Sprawdź lokalny cache zależności .NET.' }

    $frontendPath = (Get-Item -LiteralPath 'frontend').FullName.Replace([char]92, [char]47)
    $linuxFrontend = (& wsl -d Ubuntu -- wslpath -a $frontendPath)
    if ($LASTEXITCODE -ne 0) { throw 'Nie można ustalić ścieżki WSL do frontendu.' }
    & wsl -d Ubuntu --cd $linuxFrontend -- bash -lc 'export PATH="$HOME/.nvm/versions/node/v24.22.2/bin:$PATH"; export npm_config_offline=true npm_config_update_notifier=false; npm run build'
    if ($LASTEXITCODE -ne 0) { throw 'Kompilacja frontendu offline nie powiodła się. Sprawdź lokalne zależności npm.' }

    function Update-LocalImage([string]$base, [string]$source, [string]$destination, [string]$tag) {
        $containerId = (& docker create --pull=never $base).Trim()
        if ($LASTEXITCODE -ne 0 -or $containerId -notmatch '^[0-9a-f]{64}$') {
            throw "Nie można utworzyć kontenera z lokalnego obrazu $base."
        }
        try {
            & docker cp $source ($containerId + ':' + $destination)
            if ($LASTEXITCODE -ne 0) { throw "Nie można skopiować plików do $base." }
            & docker commit $containerId $tag | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Nie można utworzyć lokalnego obrazu $tag." }
        } finally {
            & docker rm $containerId | Out-Null
        }
    }

    Update-LocalImage 'university-chainloot-backend:latest' 'backend/bin/Release/net9.0/publish/.' '/app/' 'university-chainloot-backend:local-cluster'
    Update-LocalImage 'university-chainloot-frontend:latest' 'frontend/dist/.' '/usr/share/nginx/html/' 'university-chainloot-frontend:local-cluster'
    & docker compose -p chainloot-cluster -f docker-compose.cluster.yml up -d --no-build --pull never
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się uruchomić lokalnego klastra.' }
    Write-Output 'Gotowe: http://127.0.0.1:5175/ — wszystkie usługi działają lokalnie.'
} finally {
    Pop-Location
}
