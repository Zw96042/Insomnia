<#
.SYNOPSIS
    Installs Insomnia for the current user. No administrator rights needed.

.DESCRIPTION
    From a source checkout, builds Insomnia with `dotnet publish` and installs
    the result. From a release zip, installs the `app` folder next to this
    script. Either way:

      1. Asks a running Insomnia to quit, which ends any session, and stops
         if it refuses.
      2. Copies the new build into a staging folder next to the install
         folder, then swaps it in. A failed swap puts the previous build back.
      3. Registers the recovery agent (a per-user scheduled task) for the
         installed executable, and adds a Start menu shortcut.

    Installs to %LOCALAPPDATA%\Programs\Insomnia. Settings and journals live
    in %LOCALAPPDATA%\Insomnia and are kept across upgrades.

.PARAMETER App
    Install this prebuilt folder (containing Insomnia.exe) instead of
    building or using the zip's `app` folder.

.PARAMETER AllowUnverifiedOrigin
    Required to install a prebuilt build (a release zip or -App). Release
    builds are not code-signed, so this script can check that the files are
    intact but not who built them. Pass it to confirm you checked the zip
    against SHA256SUMS and its build attestation, as the README describes.
    The installed files are then unblocked so Windows does not stop the
    recovery agent at its first run.

.PARAMETER Runtime
    The .NET runtime identifier to build for from source: win-x64 or
    win-arm64. Defaults to the machine's architecture.

.PARAMETER NoLaunch
    Do not start Insomnia after installing.

.EXAMPLE
    .\scripts\install.ps1
#>
[CmdletBinding()]
param(
    [string]$App,
    [switch]$AllowUnverifiedOrigin,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-DefaultDataFolder

Assert-SupportedWindows

$installDir = Get-InstallDir
$exeName = 'Insomnia.exe'

# 1. Find or build the payload.
$prebuilt = $true
if ($App) {
    $payload = (Resolve-Path -LiteralPath $App).Path
}
elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app\Insomnia.exe')) {
    # Release zip layout: install.ps1, uninstall.ps1, common.ps1, app\
    $payload = Join-Path $PSScriptRoot 'app'
}
elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\Insomnia.sln')) {
    # Source checkout: build only from the checkout's own scripts folder.
    if (-not $Runtime) { $Runtime = Get-DefaultRuntime }
    $prebuilt = $false
    $payload = Invoke-Publish -RepoRoot (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -Runtime $Runtime
}
else {
    throw "Nothing to install: pass -App <folder>, run from a release zip, or run scripts\install.ps1 from a source checkout."
}

if ($prebuilt -and -not $AllowUnverifiedOrigin) {
    throw 'This is a prebuilt, unsigned build. Verify the zip against SHA256SUMS and its attestation (see the README), then run again with -AllowUnverifiedOrigin.'
}

$payloadExe = Join-Path $payload $exeName
if (-not (Test-Path -LiteralPath $payloadExe)) {
    throw "$payloadExe was not found."
}
Assert-ExecutableMatchesMachine -Path $payloadExe
$version = (Get-Item -LiteralPath $payloadExe).VersionInfo.ProductVersion
Write-Step "Installing Insomnia $version to $installDir"

# 2. Ask a running copy to quit. Quitting ends the session and restores the
#    machine; Insomnia refuses when something could not be restored and the
#    recovery agent is not there to finish it.
Stop-RunningInsomnia -InstallDir $installDir

# 3. Stage, then swap.
$parent = Split-Path -Parent $installDir
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$staging = Join-Path $parent (".Insomnia.staging." + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$previous = Join-Path $parent '.Insomnia.previous'
Remove-StaleStaging -Parent $parent

Copy-Item -LiteralPath $payload -Destination $staging -Recurse
try {
    if (Test-Path -LiteralPath $previous) {
        Remove-Item -LiteralPath $previous -Recurse -Force
    }
    $hadPrevious = Test-Path -LiteralPath $installDir
    if ($hadPrevious) {
        Move-Item -LiteralPath $installDir -Destination $previous
    }
    try {
        Move-Item -LiteralPath $staging -Destination $installDir
    }
    catch {
        if ($hadPrevious) {
            Move-Item -LiteralPath $previous -Destination $installDir
        }
        throw
    }
}
catch {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw "The new build could not be moved into place; the previous install is unchanged. $($_.Exception.Message)"
}

$installedExe = Join-Path $installDir $exeName
if ($prebuilt) {
    # Files from a downloaded zip carry the internet zone mark; the user
    # confirmed the origin above.
    Get-ChildItem -LiteralPath $installDir -Recurse -File | Unblock-File
}

# 4. Register the recovery agent for the installed executable. A session
#    never starts without it, so a failure here is reported but the app can
#    still retry it at the next start.
Write-Step 'Registering the recovery agent'
$arm = Start-Process -FilePath $installedExe -ArgumentList '--arm-agent' -Wait -PassThru -WindowStyle Hidden
if ($arm.ExitCode -ne 0) {
    if ($hadPrevious -and (Test-Path -LiteralPath $previous)) {
        Write-Warning "The recovery agent could not be registered (exit $($arm.ExitCode)). Restoring the previous install."
        Remove-Item -LiteralPath $installDir -Recurse -Force
        Move-Item -LiteralPath $previous -Destination $installDir
        Start-Process -FilePath $installedExe -ArgumentList '--arm-agent' -Wait -WindowStyle Hidden | Out-Null
        throw 'Install failed: the recovery agent could not be registered. See %LOCALAPPDATA%\Insomnia\Logs\insomnia.log.'
    }
    throw 'Install failed: the recovery agent could not be registered. See %LOCALAPPDATA%\Insomnia\Logs\insomnia.log.'
}

if (Test-Path -LiteralPath $previous) {
    Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. Keep the uninstaller next to the app, and add a Start menu shortcut.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'common.ps1') -Destination $installDir -Force
New-StartMenuShortcut -Target $installedExe

Write-Step "Installed. Data: $(Get-DataDir)"
if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe
}
