param(
    [switch]$SetupServerInstance,
    [switch]$RebuildCache,
    [string]$GameDir = $env:EASY_RED_2_DIR
)

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot

function Resolve-EasyRed2GameDir {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath) -and (Test-Path $ExplicitPath)) {
        return (Resolve-Path $ExplicitPath).Path
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    $steamPaths = @()
    foreach ($reg in @(
        @{ Path = "HKCU:\Software\Valve\Steam"; Name = "SteamPath" },
        @{ Path = "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam"; Name = "InstallPath" },
        @{ Path = "HKLM:\SOFTWARE\Valve\Steam"; Name = "InstallPath" }
    )) {
        try {
            $val = (Get-ItemProperty -Path $reg.Path -Name $reg.Name -ErrorAction SilentlyContinue).$($reg.Name)
            if (-not [string]::IsNullOrWhiteSpace($val)) {
                $steamPaths += ($val -replace '/', '\')
            }
        } catch {}
    }

    foreach ($sp in ($steamPaths | Select-Object -Unique)) {
        $candidates.Add((Join-Path $sp "steamapps\common\Easy Red 2"))
        $vdf = Join-Path $sp "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $content = Get-Content $vdf -Raw -ErrorAction SilentlyContinue
            foreach ($m in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
                $libPath = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $libPath "steamapps\common\Easy Red 2"))
            }
        }
    }

    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $root = $drive.Root
        $candidates.Add((Join-Path $root "SteamLibrary\steamapps\common\Easy Red 2"))
        $candidates.Add((Join-Path $root "Steam\steamapps\common\Easy Red 2"))
        $candidates.Add((Join-Path $root "Program Files (x86)\Steam\steamapps\common\Easy Red 2"))
    }

    foreach ($cand in ($candidates | Select-Object -Unique)) {
        if (Test-Path (Join-Path $cand "Easy Red 2.exe")) {
            return $cand
        }
    }
    return $null
}

function Sync-Er2CacheIfNeeded {
    param(
        [string]$WorkspaceRoot,
        [string]$ResolvedGameDir,
        [string]$CscPath,
        [string]$DotnetDir,
        [bool]$ForceRebuild
    )

    $giDir = Join-Path $WorkspaceRoot "tools\gen_interop"
    $inspectSrc = Join-Path $giDir "Inspect.cs"
    $inspectDll = Join-Path $giDir "InspectInterop.dll"
    if ((Test-Path $inspectSrc) -and (-not (Test-Path $inspectDll) -or ((Get-Item $inspectSrc).LastWriteTimeUtc -gt (Get-Item $inspectDll).LastWriteTimeUtc))) {
        Write-Host "  -> Compiling InspectInterop.dll..." -ForegroundColor Cyan
        & dotnet $CscPath /nologo /target:exe /optimize+ /nostdlib `
            "/r:$DotnetDir\System.Private.CoreLib.dll" `
            "/r:$DotnetDir\System.Runtime.dll" `
            "/r:$DotnetDir\System.Console.dll" `
            "/r:$DotnetDir\System.Collections.dll" `
            "/r:$DotnetDir\System.Linq.dll" `
            "/r:$DotnetDir\System.IO.FileSystem.dll" `
            "/r:$DotnetDir\System.Security.Cryptography.Algorithms.dll" `
            "/r:$DotnetDir\System.Security.Cryptography.Primitives.dll" `
            "/r:$DotnetDir\System.Text.RegularExpressions.dll" `
            "/r:$DotnetDir\netstandard.dll" `
            "/r:$giDir\AsmResolver.dll" `
            "/r:$giDir\AsmResolver.PE.dll" `
            "/r:$giDir\AsmResolver.PE.File.dll" `
            "/r:$giDir\AsmResolver.DotNet.dll" `
            "/r:$giDir\AssetRipper.Primitives.dll" `
            "/r:$giDir\Iced.dll" `
            "/r:$giDir\LibCpp2IL.dll" `
            "/out:$inspectDll" `
            $inspectSrc
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to compile InspectInterop.dll with exit code $LASTEXITCODE"
        }
    }

    if (-not $ResolvedGameDir -or -not (Test-Path (Join-Path $ResolvedGameDir "GameAssembly.dll"))) {
        if ($ForceRebuild) {
            throw "Cannot rebuild ER2 cache: GameAssembly.dll was not found. Pass -GameDir '<path to Easy Red 2>'."
        }
        return
    }

    $cacheDir = Join-Path $WorkspaceRoot "tools\er2_cache"
    $manifestPath = Join-Path $cacheDir "manifest.json"
    $gameAsmItem = Get-Item (Join-Path $ResolvedGameDir "GameAssembly.dll")
    $curBytes = $gameAsmItem.Length
    $curWriteUtc = $gameAsmItem.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")

    $needsRebuild = $ForceRebuild -or (-not (Test-Path $manifestPath))
    if (-not $needsRebuild) {
        try {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            if ([int64]$manifest.gameAssemblyBytes -ne $curBytes -or
                [string]$manifest.gameAssemblyLastWriteUtc -ne $curWriteUtc) {
                $needsRebuild = $true
            }
        } catch {
            $needsRebuild = $true
        }
    }

    if (-not $needsRebuild) {
        return
    }

    Write-Host "[0/2] GameAssembly change or -RebuildCache detected: re-ripping ER2 disassembly, Lua scripts & interop cache (strictly read-only)..." -ForegroundColor Yellow
    if (Test-Path $inspectDll) {
        & dotnet $inspectDll --build-cache $cacheDir $ResolvedGameDir
        if ($LASTEXITCODE -ne 0) {
            throw "InspectInterop.dll --build-cache failed with exit code $LASTEXITCODE"
        }
    }
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

function Patch-ServerUnityPlayerSingleInstance {
    param([string]$UnityPlayerPath)

    if (-not (Test-Path $UnityPlayerPath)) { return }

    $bytes = [System.IO.File]::ReadAllBytes($UnityPlayerPath)
    $modified = $false

    function Replace-ByteSequence([byte[]]$buffer, [byte[]]$oldSeq, [byte[]]$newSeq) {
        if ($oldSeq.Length -ne $newSeq.Length) {
            throw "Replacement sequence must match exact byte length."
        }
        $count = 0
        $first = $oldSeq[0]
        $max = $buffer.Length - $oldSeq.Length
        for ($i = 0; $i -le $max; $i++) {
            if ($buffer[$i] -eq $first) {
                $match = $true
                for ($j = 1; $j -lt $oldSeq.Length; $j++) {
                    if ($buffer[$i + $j] -ne $oldSeq[$j]) { $match = $false; break }
                }
                if ($match) {
                    for ($j = 0; $j -lt $newSeq.Length; $j++) {
                        $buffer[$i + $j] = $newSeq[$j]
                    }
                    $count++
                }
            }
        }
        return $count
    }

    # 1. Isolate Unity's single-instance Win32 Mutex name (-SingleInstanceMutex- -> -ServerInstanceMutex-)
    $oldMutex = [System.Text.Encoding]::ASCII.GetBytes("-SingleInstanceMutex-")
    $newMutex = [System.Text.Encoding]::ASCII.GetBytes("-ServerInstanceMutex-")
    if ((Replace-ByteSequence $bytes $oldMutex $newMutex) -gt 0) {
        $modified = $true
    }

    # 2. Isolate Unity's Win32 Window Class name (UnityWndClass -> UnitySrvClass) so vanilla client FindWindowW never focuses the server
    $oldWnd = [System.Text.Encoding]::Unicode.GetBytes("UnityWndClass")
    $newWnd = [System.Text.Encoding]::Unicode.GetBytes("UnitySrvClass")
    if ((Replace-ByteSequence $bytes $oldWnd $newWnd) -gt 0) {
        $modified = $true
    }

    if ($modified) {
        [System.IO.File]::WriteAllBytes($UnityPlayerPath, $bytes)
        Write-Host "  -> Isolated single-instance mutex & window class in server_instance\UnityPlayer.dll" -ForegroundColor Green
    }
}

$resolvedGameDir = Resolve-EasyRed2GameDir -ExplicitPath $GameDir

$bepInExRoot = Join-Path $workspace "tools\bepinex\extracted"
$dotnetDir = Join-Path $bepInExRoot "dotnet"
$coreDir = Join-Path $bepInExRoot "BepInEx\core"
$interopDir = Join-Path $bepInExRoot "BepInEx\interop"
$buildDir = Join-Path $workspace "build"
$csc = Resolve-CscPath -WorkspaceRoot $workspace

Sync-Er2CacheIfNeeded -WorkspaceRoot $workspace -ResolvedGameDir $resolvedGameDir -CscPath $csc -DotnetDir $dotnetDir -ForceRebuild:$RebuildCache.IsPresent

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

Write-Host "[1/2] Compiling EasyRed2HeadlessServer.dll..." -ForegroundColor Cyan
$sourceFiles = Get-ChildItem -Path "$workspace\src\EasyRed2Headless" -Filter "*.cs" -Recurse | Select-Object -ExpandProperty FullName
$cscArgs = @(
    $csc,
    "/nologo",
    "/target:library",
    "/optimize+",
    "/nostdlib",
    "/r:$dotnetDir\System.Private.CoreLib.dll",
    "/r:$dotnetDir\System.Runtime.dll",
    "/r:$dotnetDir\System.Collections.dll",
    "/r:$dotnetDir\netstandard.dll",
    "/r:$coreDir\BepInEx.Core.dll",
    "/r:$coreDir\BepInEx.Unity.Common.dll",
    "/r:$coreDir\BepInEx.Unity.IL2CPP.dll",
    "/r:$coreDir\0Harmony.dll",
    "/r:$coreDir\Il2CppInterop.Runtime.dll",
    "/r:$interopDir\Il2Cppmscorlib.dll",
    "/r:$interopDir\Il2CppSystem.dll",
    "/r:$interopDir\Il2CppSystem.Core.dll",
    "/r:$interopDir\UnityEngine.CoreModule.dll",
    "/r:$interopDir\UnityEngine.AudioModule.dll",
    "/r:$interopDir\UnityEngine.AnimationModule.dll",
    "/r:$interopDir\UnityEngine.IMGUIModule.dll",
    "/r:$interopDir\UnityEngine.InputLegacyModule.dll",
    "/r:$interopDir\UnityEngine.PhysicsModule.dll",
    "/r:$interopDir\UnityEngine.TerrainPhysicsModule.dll",
    "/r:$interopDir\UnityEngine.TextRenderingModule.dll",
    "/r:$interopDir\UnityEngine.UI.dll",
    "/r:$interopDir\UnityEngine.UIModule.dll",
    "/r:$interopDir\Photon3Unity3D.dll",
    "/r:$interopDir\PhotonUnityNetworking.dll",
    "/r:$interopDir\PhotonRealtime.dll",
    "/r:$interopDir\com.rlabrecque.steamworks.net.dll",
    "/r:$interopDir\Assembly-CSharp.dll",
    "/out:$buildDir\EasyRed2HeadlessServer.dll"
) + $sourceFiles

& dotnet $cscArgs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$verifySrc = Join-Path $workspace "tools\verify_patches\VerifyPatches.cs"
if (Test-Path $verifySrc) {
    $verifyDll = Join-Path $buildDir "VerifyPatches.dll"
    $cscRuntimeConfig = Join-Path (Split-Path $csc -Parent) "csc.runtimeconfig.json"
    if (-not (Test-Path $verifyDll) -or ((Get-Item $verifySrc).LastWriteTimeUtc -gt (Get-Item $verifyDll).LastWriteTimeUtc)) {
        & dotnet $csc /nologo /target:exe /optimize+ /nostdlib `
            "/r:$dotnetDir\System.Private.CoreLib.dll" `
            "/r:$dotnetDir\System.Runtime.dll" `
            "/r:$dotnetDir\System.Console.dll" `
            "/r:$dotnetDir\System.Collections.dll" `
            "/r:$dotnetDir\System.Linq.dll" `
            "/r:$dotnetDir\System.Reflection.dll" `
            "/r:$dotnetDir\System.IO.FileSystem.dll" `
            "/r:$dotnetDir\netstandard.dll" `
            "/out:$verifyDll" `
            $verifySrc
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to compile VerifyPatches.dll"
        }
    }
    & dotnet exec --runtimeconfig $cscRuntimeConfig $verifyDll "$buildDir\EasyRed2HeadlessServer.dll" $interopDir $coreDir $dotnetDir
    if ($LASTEXITCODE -ne 0) {
        throw "VerifyPatches failed! Fix Harmony signatures, shared>1 targets, Animator strings, or static code safety issues."
    }
}

Write-Host "  -> Built $buildDir\EasyRed2HeadlessServer.dll" -ForegroundColor Green

$aiDll = Join-Path $bepInExRoot "BepInEx\plugins\EasyRed2HumanAI.dll"
if (Test-Path $aiDll) {
    Copy-Item -Force $aiDll (Join-Path $buildDir "EasyRed2HumanAI.dll")
}

$serverDir = Join-Path $workspace "server_instance"
if (Test-Path (Join-Path $serverDir "UnityPlayer.dll")) {
    Patch-ServerUnityPlayerSingleInstance -UnityPlayerPath (Join-Path $serverDir "UnityPlayer.dll")
}
if (Test-Path (Join-Path $serverDir "BepInEx\plugins")) {
    Copy-Item -Force "$buildDir\EasyRed2HeadlessServer.dll" (Join-Path $serverDir "BepInEx\plugins\EasyRed2HeadlessServer.dll")
}

if ($SetupServerInstance) {
    Write-Host "[2/2] Setting up isolated server_instance staging directory..." -ForegroundColor Cyan
    $resolvedGameDir = Resolve-EasyRed2GameDir -ExplicitPath $GameDir
    if (-not $resolvedGameDir) {
        throw "Could not locate Easy Red 2 installation directory. Pass -GameDir 'path\to\Easy Red 2'."
    }

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

    Patch-ServerUnityPlayerSingleInstance -UnityPlayerPath (Join-Path $serverDir "UnityPlayer.dll")

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
