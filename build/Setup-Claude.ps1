# Sets up Claude for AutoCAD, Civil 3D and Plant 3D through Nexus (run by Setup-Claude.cmd).
# Every step says what it did; it stops with a clear message when something needs you.
# Continue: messages from git/dotnet/claude on stderr must not stop the script (Windows PowerShell 5.1).
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$hubDir = Join-Path $env:LOCALAPPDATA 'Nexus\Hub'
$server = Join-Path $hubDir 'nexus-mcp.exe'

function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "    OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host ''; Write-Host "STOPPED: $text" -ForegroundColor Red; exit 1 }

# 1. Programs that lock the add-in files
Step 'Checking that AutoCAD, Civil 3D, Plant 3D and Revit are closed'
while ($true) {
    $open = Get-Process -Name acad, Revit -ErrorAction SilentlyContinue
    if (-not $open) { Ok 'They are closed.'; break }
    Warn ("Still open: " + (($open | Select-Object -ExpandProperty ProcessName -Unique) -join ', ') + '. Save your work and close them.')
    Read-Host '    Press Enter when they are closed'
}

# 2. Latest code
Step 'Getting the latest Nexus code'
if (Get-Command git -ErrorAction SilentlyContinue) {
    Push-Location $root
    try {
        git pull --ff-only
        if ($LASTEXITCODE -eq 0) { Ok 'Up to date.' } else { Warn 'git pull did not finish (local changes?). Building what is here.' }
    } finally { Pop-Location }
} else {
    Warn 'git was not found; building the code that is in this folder. (Pull in Visual Studio first if you have not.)'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK was not found. Install Visual Studio 2026 (or the .NET 10 SDK) and run this again.'
}

# 3. Add-ins (AutoCAD, Civil 3D, Plant 3D, Revit) for the years installed here
Step 'Building and installing the add-ins (a few minutes)'
& cmd /c "`"$root\build\Build-AllYears.cmd`""
if ($LASTEXITCODE -ne 0) { Warn 'Some add-in years did not build (see above). Years you have installed may still be fine; continuing.' }
else { Ok 'Add-ins installed.' }

# 4. Hub and the MCP server
Step 'Building and installing the hub and nexus-mcp.exe'
& cmd /c "`"$root\build\Publish-Hub.cmd`" install"
if ($LASTEXITCODE -ne 0) { Fail 'The hub did not build. Copy the messages above and send them to Claude.' }
if (-not (Test-Path $server)) {
    Warn 'nexus-mcp.exe was not installed with the hub; building it straight into the hub folder.'
    & dotnet publish "$root\src\Nexus.Mcp\Nexus.Mcp.csproj" -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded -o $hubDir -nologo -v:minimal
    if (-not (Test-Path $server)) { Fail 'nexus-mcp.exe could not be built. Copy the messages above and send them to Claude.' }
}
Ok "Server: $server"

# 5. Claude Desktop: add "nexus" to every config it may read (standard and Microsoft Store installs)
Step 'Adding nexus to Claude Desktop'
$configs = @()
$roaming = Join-Path $env:APPDATA 'Claude'
if (Test-Path $roaming) { $configs += (Join-Path $roaming 'claude_desktop_config.json') }
$packages = Join-Path $env:LOCALAPPDATA 'Packages'
if (Test-Path $packages) {
    Get-ChildItem $packages -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
        $dir = Join-Path $_.FullName 'LocalCache\Roaming\Claude'
        if (Test-Path $dir) { $configs += (Join-Path $dir 'claude_desktop_config.json') }
    }
}
if ($configs.Count -eq 0) {
    New-Item -ItemType Directory -Force -Path $roaming | Out-Null
    $configs += (Join-Path $roaming 'claude_desktop_config.json')
}
foreach ($file in $configs) {
    $json = $null
    if (Test-Path $file) {
        Copy-Item $file "$file.nexus-backup" -Force -ErrorAction Stop
        $text = Get-Content $file -Raw
        if ($text -and $text.Trim()) {
            try { $json = $text | ConvertFrom-Json -ErrorAction Stop } catch { Fail "$file is not valid JSON. Open it with Edit config in Claude Desktop, fix it, and run this again." }
        }
    }
    if (-not $json) { $json = [pscustomobject]@{} }
    if (-not ($json.PSObject.Properties.Name -contains 'mcpServers')) {
        $json | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    $entry = [pscustomobject]@{ command = $server; args = @() }
    if ($json.mcpServers.PSObject.Properties.Name -contains 'nexus') { $json.mcpServers.nexus = $entry }
    else { $json.mcpServers | Add-Member -NotePropertyName nexus -NotePropertyValue $entry }
    $out = $json | ConvertTo-Json -Depth 50
    try { [System.IO.File]::WriteAllText($file, $out, (New-Object System.Text.UTF8Encoding($false))) }
    catch { Fail "Could not write $file : $($_.Exception.Message)" }
    $names = ($json.mcpServers.PSObject.Properties.Name) -join ', '
    Ok "$file  (servers: $names)"
}

# 6. Claude Code (if installed)
if (Get-Command claude -ErrorAction SilentlyContinue) {
    Step 'Adding nexus to Claude Code'
    & claude mcp remove nexus --scope user 2>$null | Out-Null
    & claude mcp add nexus --scope user -- "$server"
    if ($LASTEXITCODE -eq 0) { Ok 'Claude Code knows nexus (new sessions).' } else { Warn ("Claude Code did not take it; add it later with: claude mcp add nexus --scope user -- `"$server`"") }
}

# 7. Restart Claude Desktop
Step 'Restart Claude Desktop'
$claude = Get-Process -Name claude -ErrorAction SilentlyContinue
if ($claude) {
    $path = ($claude | Where-Object { $_.Path } | Select-Object -First 1).Path
    $answer = Read-Host '    Claude Desktop is open. Restart it now so it loads nexus? (Y/n)'
    if ($answer -notmatch '^[nN]') {
        $claude | Stop-Process -Force
        Start-Sleep -Seconds 2
        $started = $false
        if ($path) { try { Start-Process $path -ErrorAction Stop; $started = $true } catch { } }
        if ($started) { Ok 'Claude Desktop restarted.' }
        else { Warn 'Claude Desktop was closed: start it again from the Start menu now.' }
    } else {
        Warn 'Quit Claude Desktop from its tray icon (right-click > Quit) and start it again.'
    }
} else {
    Ok 'Claude Desktop is not running: start it now.'
}

Write-Host ''
Write-Host 'ALL DONE.' -ForegroundColor Green
Write-Host '  1. Open AutoCAD (or Civil 3D / Plant 3D) with a drawing.'
Write-Host '  2. In Claude Desktop: Settings > Developer should show "nexus  Running".'
Write-Host '  3. Ask Claude: "Use nexus_status", then "Draw a 10 x 10 square polyline at 0,0".'
