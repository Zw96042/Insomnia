<#
.SYNOPSIS
    Removes Insomnia for the current user.

.DESCRIPTION
    1. Asks a running Insomnia to quit, which ends any session.
    2. Runs the recovery agent once to restore anything still journaled. If
       anything could not be restored, it stops and removes nothing, so the
       agent and the journal stay to finish the job.
    3. Removes the recovery task, the launch-at-login entry, the Start menu
       shortcut and the install folder.

    Settings and logs in %LOCALAPPDATA%\Insomnia are kept unless -Purge is
    given. Even with -Purge, a journal holding saved volumes for audio
    devices that are away is kept, so a later install can restore them.

.PARAMETER Purge
    Also delete settings, logs and saved hotspot passwords.
#>
[CmdletBinding()]
param(
    [switch]$Purge
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-DefaultDataFolder

$installDir = Get-InstallDir
$exe = Join-Path $installDir 'Insomnia.exe'
$dataDir = Get-DataDir

Stop-RunningInsomnia -InstallDir $installDir

if (Test-Path -LiteralPath $exe) {
    Write-Step 'Restoring anything still journaled'
    $recover = Start-Process -FilePath $exe -ArgumentList '--recover' -Wait -PassThru -WindowStyle Hidden
    switch ($recover.ExitCode) {
        0 { }
        2 { throw 'The recovery lock is busy. Wait a minute and run this again.' }
        4 {
            throw 'Insomnia changed the power mode for the power source not in use, and Windows only lets it be restored while that source is active. Plug in or unplug the charger, then run this again. Nothing was removed.'
        }
        default {
            throw "Some settings could not be restored (exit $($recover.ExitCode)). Nothing was removed; see $dataDir\Logs\insomnia.log."
        }
    }
    if (Test-Path -LiteralPath (Join-Path $dataDir 'session.json')) {
        throw "A session is still recorded in $dataDir\session.json. End it from the tray menu, then run this again."
    }

    Write-Step 'Removing the recovery agent'
    $remove = Start-Process -FilePath $exe -ArgumentList '--remove-agent' -Wait -PassThru -WindowStyle Hidden
    if ($remove.ExitCode -ne 0) {
        Write-Warning 'The recovery task could not be removed. Delete "Insomnia\Recovery-<your SID>" in Task Scheduler.'
    }
}
else {
    Write-Warning "$exe was not found; skipping recovery and task removal."
}

Write-Step 'Removing launch at login'
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Insomnia' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'Insomnia' -ErrorAction SilentlyContinue

$shortcut = Get-StartMenuShortcut
if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut -Force
}

Write-Step "Removing $installDir"
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
$parent = Split-Path -Parent $installDir
Remove-StaleStaging -Parent $parent
$previous = Join-Path $parent '.Insomnia.previous'
if (Test-Path -LiteralPath $previous) {
    Remove-Item -LiteralPath $previous -Recurse -Force
}

if ($Purge) {
    Write-Step 'Removing saved hotspot passwords'
    $targets = cmdkey /list | Select-String -Pattern 'Target:\s*(?:LegacyGeneric:target=)?(Insomnia/hotspot/.+)$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() }
    foreach ($target in $targets) {
        cmdkey /delete:$target | Out-Null
    }

    $state = Join-Path $dataDir 'state.json'
    if (Test-Path -LiteralPath $state) {
        Write-Warning "Keeping ${state}: it holds saved volumes for audio devices that are not connected. A later install restores them."
        Get-ChildItem -LiteralPath $dataDir -Force | Where-Object { $_.Name -ne 'state.json' } |
            Remove-Item -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $dataDir) {
        Write-Step "Removing $dataDir"
        Remove-Item -LiteralPath $dataDir -Recurse -Force
    }
}
else {
    Write-Host "Settings and logs are kept in $dataDir. Run with -Purge to remove them."
}

Write-Step 'Insomnia is uninstalled.'
