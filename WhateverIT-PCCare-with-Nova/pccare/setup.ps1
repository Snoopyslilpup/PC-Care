# =====================================================================
#  Whatever IT PC Care - fully automated setup / launcher
# ---------------------------------------------------------------------
#  Does EVERYTHING needed to launch the app with no manual steps:
#    * finds a usable .NET 8 SDK, or installs one locally (no admin)
#    * builds the self-contained app the first time
#    * launches it
#  Modes (passed from setup.bat):
#    (none) / run      -> build if needed, then launch the app
#    rebuild           -> force a clean rebuild, then launch
#    build             -> just build the sellable .exe (no launch)
#    installer         -> build + auto-install Inno Setup + compile the
#                         installer, then OPEN the finished wizard
#    keygen a b c      -> run the licence key generator
# =====================================================================
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$Root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj      = Join-Path $Root 'src\WhateverITPCCare.csproj'
$KeyProj   = Join-Path $Root 'keygen\KeyGen.csproj'
$PublishDir= Join-Path $Root 'publish'
$Exe       = Join-Path $PublishDir 'WhateverITPCCare.exe'
$LocalNet  = Join-Path $Root '.dotnet'
$IsccLocal = Join-Path $Root '.innosetup'

function Say($m, $c='Cyan') { Write-Host $m -ForegroundColor $c }
function Warn($m)           { Write-Host $m -ForegroundColor Yellow }
function Die($m)            { Write-Host $m -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------
# 1. Find or install a .NET 8+ SDK
# ---------------------------------------------------------------------
function Get-Dotnet {
    # a) local private copy from a previous run
    $local = Join-Path $LocalNet 'dotnet.exe'
    if (Test-Path $local) { return $local }

    # b) a system dotnet with an 8.0+ SDK already installed
    $sys = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($sys) {
        try {
            $sdks = & dotnet --list-sdks 2>$null
            foreach ($line in $sdks) {
                if ($line -match '^(\d+)\.') { if ([int]$Matches[1] -ge 8) { return 'dotnet' } }
            }
        } catch { }
    }
    return $null
}

function Install-Dotnet {
    Say ''
    Say 'The .NET 8 runtime/SDK was not found. Installing it automatically...' 'Cyan'
    Say '(This is a one-time ~200 MB download into this folder - no admin needed.)' 'DarkGray'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    $urls = @('https://dot.net/v1/dotnet-install.ps1',
              'https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.ps1')
    $got = $false
    foreach ($u in $urls) {
        try { Invoke-WebRequest -Uri $u -OutFile $script -UseBasicParsing -TimeoutSec 60; $got = $true; break }
        catch { Warn "  could not reach $u" }
    }
    if (-not $got) {
        Die @"

[!] Could not download the .NET installer (your network may be blocking it).
    Please install the .NET 8 SDK (x64) once from:
        https://dotnet.microsoft.com/download/dotnet/8.0
    then run setup again.
"@
    }
    try {
        # Antivirus scanning each of the ~20,000 files as the SDK extracts is the
        # #1 reason this step LOOKS frozen on 'Extracting the archive'. If we're
        # running elevated, exclude the target folder from Microsoft Defender so
        # extraction runs at full speed. (Silently ignored if not permitted.)
        try { Add-MpPreference -ExclusionPath $LocalNet -ErrorAction SilentlyContinue } catch { }
        Say 'Installing .NET - the "Extracting the archive" step can take several' 'DarkGray'
        Say 'minutes and shows no progress. This is normal; please wait.' 'DarkGray'
        & $script -Channel 8.0 -InstallDir $LocalNet -NoPath
    } catch {
        Die "[!] .NET install failed: $($_.Exception.Message)"
    }
    $local = Join-Path $LocalNet 'dotnet.exe'
    if (-not (Test-Path $local)) { Die '[!] .NET install did not produce dotnet.exe.' }
    Say '[OK] .NET installed locally.' 'Green'
    return $local
}

# ---------------------------------------------------------------------
# 1b. Find or install the Inno Setup compiler (ISCC) - only used by the
#     'installer' mode. Mirrors the .NET auto-install: portable, no admin.
# ---------------------------------------------------------------------
function Get-Iscc {
    # a) local portable copy from a previous run
    $localIscc = Join-Path $IsccLocal 'ISCC.exe'
    if (Test-Path $localIscc) { return $localIscc }
    # b) on PATH
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # c) standard install locations
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe")) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    return $null
}

function Install-Iscc {
    Say ''
    Say 'The Inno Setup compiler was not found. Installing it automatically...' 'Cyan'
    Say '(One-time ~6 MB download into this folder - no admin needed.)' 'DarkGray'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $dl = Join-Path $env:TEMP 'innosetup-latest.exe'
    $urls = @('https://jrsoftware.org/download.php/is.exe')
    $got = $false
    foreach ($u in $urls) {
        try { Invoke-WebRequest -Uri $u -OutFile $dl -UseBasicParsing -TimeoutSec 120; $got = $true; break }
        catch { Warn "  could not reach $u" }
    }
    if (-not $got) {
        Die @"

[!] Could not download Inno Setup (your network may be blocking it).
    Please install it once from:
        https://jrsoftware.org/isdl.php
    then run setup again.
"@
    }
    # Portable install (no admin, no registry) into a local .innosetup folder.
    try {
        Start-Process -FilePath $dl -Wait -ArgumentList @(
            '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
            '/PORTABLE=1', "/DIR=$IsccLocal") | Out-Null
    } catch {
        Die "[!] Inno Setup install failed: $($_.Exception.Message)"
    }
    $localIscc = Join-Path $IsccLocal 'ISCC.exe'
    if (-not (Test-Path $localIscc)) {
        Die '[!] Inno Setup install did not produce ISCC.exe.'
    }
    Say '[OK] Inno Setup installed locally.' 'Green'
    return $localIscc
}

$Dotnet = Get-Dotnet
if (-not $Dotnet) { $Dotnet = Install-Dotnet }
Say "Using .NET: $Dotnet" 'DarkGray'

# ---------------------------------------------------------------------
# 2. Dispatch on mode
# ---------------------------------------------------------------------
if ($args.Count -ge 1) { $mode = $args[0].ToString().ToLower() } else { $mode = 'run' }

function Build-App {
    Say ''
    Say 'Building Whatever IT PC Care (first run can take a minute)...' 'Cyan'
    & $Dotnet publish $Proj -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { Die '[!] Build failed - see the messages above.' }
    if (-not (Test-Path $Exe)) { Die '[!] Build finished but the .exe is missing.' }
    Say "[OK] Built: $Exe" 'Green'
}

switch ($mode) {
    'build' {
        Build-App
        Say ''; Say 'Your sellable app is ready at:' 'Green'; Say "   $Exe"
    }
    'rebuild' {
        if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
        Build-App
        Say 'Launching...' 'Cyan'; Start-Process $Exe
    }
    'installer' {
        Build-App
        $Iscc = Get-Iscc
        if (-not $Iscc) { $Iscc = Install-Iscc }
        Say "Using Inno Setup: $Iscc" 'DarkGray'
        Say ''
        Say 'Compiling the Windows installer...' 'Cyan'
        & $Iscc (Join-Path $Root 'installer.iss')
        if ($LASTEXITCODE -ne 0) { Die '[!] Installer compile failed - see the messages above.' }
        $SetupExe = Join-Path $Root 'Output\WhateverITPCCare-Setup.exe'
        if (-not (Test-Path $SetupExe)) { Die '[!] Compile finished but the installer .exe is missing.' }
        Say ''
        Say '[OK] Installer created:' 'Green'
        Say "   $SetupExe"
        Say ''
        Say 'Opening the installer wizard now so you can see it...' 'Green'
        Start-Process $SetupExe
        Say ''
        Say 'TIP: this Output\WhateverITPCCare-Setup.exe is the ONLY file you' 'DarkGray'
        Say '     upload to your website / send to customers. They see just the' 'DarkGray'
        Say '     wizard - never this command window.' 'DarkGray'
    }
    'keygen' {
        $kargs = @($args[1..($args.Count-1)])
        & $Dotnet run --project $KeyProj -- @kargs
    }
    default {
        if (-not (Test-Path $Exe)) { Build-App }
        Say ''; Say 'Launching Whatever IT PC Care...' 'Green'
        Start-Process $Exe
        Say 'The app window should now be open. You can close this window.' 'DarkGray'
    }
}
