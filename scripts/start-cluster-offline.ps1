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
            throw "Missing local image $image. This script does not download images."
        }
    }

    & dotnet publish backend/ChainLoot.Backend.csproj --no-restore -c Release -p:IsTransformWebConfigDisabled=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Offline backend build failed. Check the local .NET dependency cache.' }

    $frontendPath = (Get-Item -LiteralPath 'frontend').FullName.Replace([char]92, [char]47)
    $linuxFrontend = (& wsl -d Ubuntu -- wslpath -a $frontendPath)
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the frontend WSL path.' }
    & wsl -d Ubuntu --cd $linuxFrontend -- bash -lc 'export PATH="$HOME/.nvm/versions/node/v24.22.2/bin:$PATH"; export npm_config_offline=true npm_config_update_notifier=false; npm run build'
    if ($LASTEXITCODE -ne 0) { throw 'Offline frontend build failed. Check local npm dependencies.' }

    function Update-LocalImage([string]$base, [string]$source, [string]$destination, [string]$tag) {
        $containerId = (& docker create --pull=never $base).Trim()
        if ($LASTEXITCODE -ne 0 -or $containerId -notmatch '^[0-9a-f]{64}$') {
            throw "Could not create a container from local image $base."
        }
        try {
            & docker cp $source ($containerId + ':' + $destination)
            if ($LASTEXITCODE -ne 0) { throw "Could not copy files to $base." }
            & docker commit $containerId $tag | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not create local image $tag." }
        } finally {
            & docker rm $containerId | Out-Null
        }
    }

    Update-LocalImage 'university-chainloot-backend:latest' 'backend/bin/Release/net9.0/publish/.' '/app/' 'university-chainloot-backend:local-cluster'
    Update-LocalImage 'university-chainloot-frontend:latest' 'frontend/dist/.' '/usr/share/nginx/html/' 'university-chainloot-frontend:local-cluster'
    & docker compose -p chainloot-cluster -f docker-compose.cluster.yml up -d --no-build --pull never
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the local cluster.' }
    Write-Output 'Ready: http://127.0.0.1:5175/ — all services run locally.'
} finally {
    Pop-Location
}
