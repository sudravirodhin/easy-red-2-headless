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

Write-Host "[1/2] Compiling EasyRed2HeadlessServer.dll..." -ForegroundColor Cyan
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
    /r:"$interopDir\Il2CppSystem.dll" `
    /r:"$interopDir\Il2CppSystem.Core.dll" `
    /r:"$interopDir\UnityEngine.CoreModule.dll" `
    /r:"$interopDir\UnityEngine.AudioModule.dll" `
    /r:"$interopDir\UnityEngine.IMGUIModule.dll" `
    /r:"$interopDir\UnityEngine.InputLegacyModule.dll" `
    /r:"$interopDir\UnityEngine.PhysicsModule.dll" `
    /r:"$interopDir\UnityEngine.TextRenderingModule.dll" `
    /r:"$interopDir\UnityEngine.UI.dll" `
    /r:"$interopDir\UnityEngine.UIModule.dll" `
    /r:"$interopDir\Photon3Unity3D.dll" `
    /r:"$interopDir\PhotonUnityNetworking.dll" `
    /r:"$interopDir\PhotonRealtime.dll" `
    /r:"$interopDir\com.rlabrecque.steamworks.net.dll" `
    /r:"$interopDir\Assembly-CSharp.dll" `
    /out:"$buildDir\EasyRed2HeadlessServer.dll" `
    "$workspace\src\EasyRed2Headless\Plugin.cs" `
    "$workspace\src\EasyRed2Headless\HeadlessConfig.cs" `
    "$workspace\src\EasyRed2Headless\HeadlessPatches.cs" `
    "$workspace\src\EasyRed2Headless\HeadlessServerController.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "  -> Built $buildDir\EasyRed2HeadlessServer.dll" -ForegroundColor Green

$aiDll = Join-Path $bepInExRoot "BepInEx\plugins\EasyRed2HumanAI.dll"
if (Test-Path $aiDll) {
    Copy-Item -Force $aiDll (Join-Path $buildDir "EasyRed2HumanAI.dll")
}

if ($SetupServerInstance) {
    Write-Host "[2/2] Setting up isolated server_instance staging directory..." -ForegroundColor Cyan
    $resolvedGameDir = Resolve-EasyRed2GameDir -ExplicitPath $GameDir
    if (-not $resolvedGameDir) {
        throw "Could not locate Easy Red 2 installation directory. Pass -GameDir 'path\to\Easy Red 2'."
    }

    $serverDir = Join-Path $workspace "server_instance"
    New-Item -ItemType Directory -Force -Path $serverDir | Out-Null

    # Copy root executables/DLLs (leaves vanilla directory 100% untouched)
    foreach ($file in @("Easy Red 2.exe", "GameAssembly.dll", "UnityPlayer.dll", "baselib.dll", "UnityCrashHandler64.exe")) {
        $srcFile = Join-Path $resolvedGameDir $file
        $dstFile = Join-Path $serverDir $file
        if ((Test-Path $srcFile) -and -not (Test-Path $dstFile)) {
            Copy-Item -Force $srcFile $dstFile
            Write-Host "  -> Copied $file"
        }
    }

    # Create steam_appid.txt for Steamworks initialization
    Set-Content -Path (Join-Path $serverDir "steam_appid.txt") -Value "1324780" -NoNewline

    # Create Directory Junctions for read-only data folders
    foreach ($folder in @("Easy Red 2_Data", "Pizzeria")) {
        $srcFolder = Join-Path $resolvedGameDir $folder
        $dstFolder = Join-Path $serverDir $folder
        if ((Test-Path $srcFolder) -and -not (Test-Path $dstFolder)) {
            cmd /c mklink /J "$dstFolder" "$srcFolder" | Out-Null
            Write-Host "  -> Linked Directory Junction: $folder"
        }
    }

    # Copy BepInEx runtime into server_instance
    Copy-Item -Recurse -Force "$bepInExRoot\*" $serverDir
    $pluginsDir = Join-Path $serverDir "BepInEx\plugins"
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    Copy-Item -Force "$buildDir\EasyRed2HeadlessServer.dll" (Join-Path $pluginsDir "EasyRed2HeadlessServer.dll")

    Write-Host "  -> Isolated Headless Server ready at: $serverDir" -ForegroundColor Green
}
