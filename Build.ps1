[CmdletBinding()]
param(
    [string]$UiLibPath,
    [string]$WorkshopPath = "F:\SteamLibrary\steamapps\workshop\content\2162800\3735218203",
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$buildError = $null

Push-Location $PSScriptRoot
try {
    if (-not $UiLibPath) {
        $uiLibSearchRoots = @($WorkshopPath)
        if ($env:SPZ2_PERSISTENT) {
            $uiLibSearchRoots += Join-Path $env:SPZ2_PERSISTENT "mods\Shapez2UILib"
        }

        foreach ($searchRoot in $uiLibSearchRoots) {
            if (-not (Test-Path -LiteralPath $searchRoot)) {
                continue
            }

            $UiLibPath = Get-ChildItem -LiteralPath $searchRoot -Recurse -Filter Shapez2UILib.dll -File |
                Select-Object -First 1 -ExpandProperty FullName
            if ($UiLibPath) {
                break
            }
        }
    }

    if (-not $UiLibPath -or -not (Test-Path -LiteralPath $UiLibPath -PathType Leaf)) {
        throw "Shapez2UILib.dll was not found. Pass its full path with -UiLibPath, or pass the Workshop item folder with -WorkshopPath."
    }

    if (-not $env:SPZ2_PATH) {
        throw "SPZ2_PATH is not set. Add --set-modding-env-vars to the shapez 2 Steam launch options, start and close the game, then reopen PowerShell."
    }

    if (-not $env:SPZ2_PERSISTENT) {
        throw "SPZ2_PERSISTENT is not set. Add --set-modding-env-vars to the shapez 2 Steam launch options, start and close the game, then reopen PowerShell."
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found. Install a current .NET SDK and reopen PowerShell."
    }

    Write-Host "Using UI library: $UiLibPath" -ForegroundColor Cyan

    & dotnet restore .\Shapez2Multiplayer.csproj
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    & dotnet build .\Shapez2Multiplayer.csproj `
        -c Release `
        "-p:SPZ2_UILIB_PATH=$UiLibPath"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    Write-Host "`nBuild completed successfully." -ForegroundColor Green
}
catch {
    $buildError = $_
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Pop-Location
    if (-not $NoPause) {
        Write-Host
        Read-Host "Press Enter to close"
    }
}

if ($buildError) {
    throw $buildError
}
