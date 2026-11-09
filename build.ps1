# AmpCast — full build: native ASIO driver (x64 + x86) → .NET app → installer.
# Requirements:
#   * Visual Studio 2022 / Build Tools 2022 with "Desktop development with C++" (MSVC + CMake)
#   * .NET SDK 10
#   * Inno Setup 6 (optional, for the installer)
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$build = Join-Path $root 'build'

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vs) {
            $c = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path $c) { return $c }
        }
    }
    throw 'CMake/MSVC not found. Install "Build Tools for Visual Studio 2022" with "Desktop development with C++".'
}

# 1. Native driver ------------------------------------------------------------
$cmake = Find-CMake
$src = Join-Path $root 'src\AmpCast.Asio'
foreach ($arch in @(@{ A = 'x64'; Dir = 'x64' }, @{ A = 'Win32'; Dir = 'x86' })) {
    $bdir = Join-Path $build "asio-cmake-$($arch.Dir)"
    & $cmake -S $src -B $bdir -A $arch.A | Out-Host
    & $cmake --build $bdir --config $Configuration | Out-Host
    if ($LASTEXITCODE) { throw "native build failed ($($arch.A))" }
    $out = Join-Path $build "asio\$($arch.Dir)"
    New-Item -ItemType Directory -Force $out | Out-Null
    Get-ChildItem (Join-Path $bdir $Configuration) -Filter 'AmpCastAsio*.dll' | Copy-Item -Destination $out -Force
}

# 2. .NET app (self-contained: users do not need to install .NET) -------------
$publish = Join-Path $build 'publish'
dotnet publish (Join-Path $root 'src\AmpCast\AmpCast.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o $publish | Out-Host
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# 3. Installer ----------------------------------------------------------------
if (-not $SkipInstaller) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc (Join-Path $root 'installer\AmpCast.iss') | Out-Host
        if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    } else {
        Write-Warning 'Inno Setup 6 not found — installer skipped (https://jrsoftware.org/isinfo.php).'
    }
}
"Done. Output: $build"
