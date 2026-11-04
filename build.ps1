param(
    [switch]$SetupServerInstance,
    [string]$GameDir = $env:EASY_RED_2_DIR
)

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot

function Resolve-EasyRed2GameDir {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath) -and (Test-Path $ExplicitPath)) {
        return (Resolve-Path $ExplicitPath).Path
    }

    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $cand = Join-Path $drive.Root "SteamLibrary\steamapps\common\Easy Red 2"
        if (Test-Path (Join-Path $cand "Easy Red 2.exe")) {
            return $cand
        }
    }
    return $null
}

function Resolve-CscPath {
    param([string]$WorkspaceRoot)

    $sdkLines = & dotnet --list-sdks 2>$null
    if ($sdkLines) {
        foreach ($line in ($sdkLines | Sort-Object -Descending)) {
            if ($line -match '^(\S+)\s+\[(.+)\]$') {
                $sdkCsc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
                if (Test-Path $sdkCsc) { return $sdkCsc }
            }
        }
    }

    $localCsc = Join-Path $WorkspaceRoot "tools\roslyn\extracted\tasks\netcore\bincore\csc.dll"
    if (Test-Path $localCsc) {
        return $localCsc
    }

    throw "Could not locate Roslyn csc.dll. Install the .NET SDK or place Roslyn under tools\roslyn."
}

$bepInExRoot = Join-Path $workspace "tools\bepinex\extracted"
$dotnetDir = Join-Path $bepInExRoot "dotnet"
$coreDir = Join-Path $bepInExRoot "BepInEx\core"
$interopDir = Join-Path $bepInExRoot "BepInEx\interop"
$buildDir = Join-Path $workspace "build"
$csc = Resolve-CscPath -WorkspaceRoot $workspace

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

Write-Host "[1/1] Compiling EasyRed2HeadlessServer.dll..." -ForegroundColor Cyan
dotnet $csc /nologo /target:library /optimize+ /nostdlib `
    /r:"$dotnetDir\System.Private.CoreLib.dll" `
    /r:"$dotnetDir\System.Runtime.dll" `
    /r:"$dotnetDir\System.Collections.dll" `
    /r:"$dotnetDir\netstandard.dll" `
    /r:"$coreDir\BepInEx.Core.dll" `
    /r:"$coreDir\BepInEx.Unity.Common.dll" `
    /r:"$coreDir\BepInEx.Unity.IL2CPP.dll" `
    /r:"$coreDir\0Harmony.dll" `
    /r:"$coreDir\Il2CppInterop.Runtime.dll" `
    /r:"$interopDir\Il2Cppmscorlib.dll" `
    /r:"$interopDir\UnityEngine.CoreModule.dll" `
    /r:"$interopDir\UnityEngine.AudioModule.dll" `
    /r:"$interopDir\UnityEngine.IMGUIModule.dll" `
    /r:"$interopDir\UnityEngine.InputLegacyModule.dll" `
    /r:"$interopDir\UnityEngine.PhysicsModule.dll" `
    /r:"$interopDir\UnityEngine.UI.dll" `
    /r:"$interopDir\UnityEngine.UIModule.dll" `
    /r:"$interopDir\PhotonUnityNetworking.dll" `
    /r:"$interopDir\PhotonRealtime.dll" `
    /r:"$interopDir\com.rlabrecque.steamworks.net.dll" `
    /r:"$interopDir\Assembly-CSharp.dll" `
    /out:"$buildDir\EasyRed2HeadlessServer.dll" `
    "$workspace\src\EasyRed2Headless\Plugin.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "  -> Built $buildDir\EasyRed2HeadlessServer.dll" -ForegroundColor Green
