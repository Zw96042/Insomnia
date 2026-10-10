# Shared helpers for install.ps1 and uninstall.ps1. Dot-sourced, not run.

function Write-Step([string]$Message) {
    Write-Host "==> $Message"
}

function Get-InstallDir {
    Join-Path $env:LOCALAPPDATA 'Programs\Insomnia'
}

function Get-DataDir {
    Join-Path $env:LOCALAPPDATA 'Insomnia'
}

# INSOMNIA_HOME points tests and one-off runs at another folder. The
# installed app and its recovery task always use the default one, so
# installing or uninstalling with it set would check one journal and then
# change the agent that protects another. Stop before anything changes.
function Assert-DefaultDataFolder {
    if (-not [string]::IsNullOrWhiteSpace($env:INSOMNIA_HOME)) {
        throw "INSOMNIA_HOME is set ($env:INSOMNIA_HOME). Remove it from this shell (Remove-Item Env:INSOMNIA_HOME) and run this again."
    }
}

function Get-StartMenuShortcut {
    Join-Path ([Environment]::GetFolderPath('Programs')) 'Insomnia.lnk'
}

function Assert-SupportedWindows {
    $os = [Environment]::OSVersion.Version
    if ($os.Major -lt 10 -or ($os.Major -eq 10 -and $os.Build -lt 19041)) {
        throw "Insomnia needs Windows 10 version 2004 (build 19041) or later; this is build $($os.Build)."
    }
}

function Get-DefaultRuntime {
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
}

# Reads the PE header's machine field so an x64 build is not installed on
# Arm64 by accident (it would run under emulation) or the reverse (it would
# not run at all).
function Assert-ExecutableMatchesMachine([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $built = switch ($machine) { 0x8664 { 'win-x64' } 0xAA64 { 'win-arm64' } default { 'unknown' } }
    $here = Get-DefaultRuntime
    if ($built -eq 'unknown') {
        throw "$Path is not an x64 or Arm64 executable (machine 0x$('{0:X4}' -f $machine))."
    }
    if ($built -ne $here) {
        throw "$Path is built for $built but this PC is $here. Use the matching release zip."
    }
}

function Invoke-Publish([string]$RepoRoot, [string]$Runtime) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw 'Building from source needs the .NET 10 SDK: https://dotnet.microsoft.com/download'
    }
    $out = Join-Path $RepoRoot "artifacts\publish\$Runtime"
    Write-Step "Building Insomnia for $Runtime"
    & $dotnet.Source publish (Join-Path $RepoRoot 'src\Insomnia.App\Insomnia.App.csproj') `
        -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true `
        -o $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    return $out
}

# Asks the running app to quit through its own --quit command, which signals
# the instance and waits for it. Exit 0: it quit or was not running. Any
# other exit: it refused, and the installer stops without changing anything.
function Stop-RunningInsomnia([string]$InstallDir) {
    $running = Get-Process -Name 'Insomnia' -ErrorAction SilentlyContinue
    if (-not $running) { return }
    $exe = Join-Path $InstallDir 'Insomnia.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        $exe = ($running | Select-Object -First 1).Path
    }
    Write-Step 'Asking Insomnia to quit (this ends any running session)'
    $quit = Start-Process -FilePath $exe -ArgumentList '--quit' -Wait -PassThru -WindowStyle Hidden
    if ($quit.ExitCode -ne 0) {
        throw 'Insomnia did not quit: it is still restoring settings. Open its tray menu to see why, then run this again.'
    }
    # The process may take a moment to exit after it answered.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'Insomnia' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name 'Insomnia' -ErrorAction SilentlyContinue) {
        throw 'Insomnia is still running 15 s after agreeing to quit. Quit it from the tray menu and run this again.'
    }
}

function Remove-StaleStaging([string]$Parent) {
    Get-ChildItem -LiteralPath $Parent -Directory -Force -Filter '.Insomnia.staging.*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

function New-StartMenuShortcut([string]$Target) {
    $path = Get-StartMenuShortcut
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.Description = 'Keep your PC awake for a timed session'
    $link.Save()
    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}
