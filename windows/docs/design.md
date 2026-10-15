# Insomnia for Windows: design

This port brings [krishhgg/Insomnia](https://github.com/krishhgg/Insomnia), a
macOS menu bar app for timed awake sessions, to Windows 10 (22H2) and
Windows 11. It keeps the original's goals and safety model: a session always
has an end time, the PC stays awake with the lid closed only during a
session, and every change Insomnia makes is journaled before it is made and
undone from the journal, never from memory.

The macOS spec (`docs/spec.md` upstream) is the behavioral reference. This
document records where Windows works differently and how each feature maps.

## Goals

1. Hold off sleep for a fixed, user-chosen duration, never as a toggle, and
   keep the PC running with the lid closed during that time.
2. Reduce battery and heat while the lid is closed without slowing the agents
   doing the work.
3. Shorten Wi-Fi to hotspot handoffs so agent API retries succeed.
4. Journal every change and recover after a crash, reporting incomplete
   restoration rather than promising success.

Use on a stable, well-ventilated surface. Never in a closed bag.

## Platform

- Windows 10 22H2 (build 19045) or Windows 11, x64 or Arm64.
- .NET 10, WPF for windows, WinForms `NotifyIcon` for the tray icon, WinRT
  projections (`net10.0-windows10.0.19041.0`) for Wi-Fi and battery reports.
  No third-party runtime dependencies.
- Installed per user to `%LOCALAPPDATA%\Programs\Insomnia`, no administrator
  rights needed. Data in `%LOCALAPPDATA%\Insomnia`.

## Projects

| Project | Target | Role |
| --- | --- | --- |
| `Insomnia.Core` | `net10.0` | Models, store, journal, rules, the session engine. Platform-neutral and unit tested on any OS. |
| `Insomnia.Windows` | `net10.0-windows10.0.19041.0` | Implementations of `Insomnia.Core.Platform` contracts (P/Invoke, COM, WinRT). |
| `Insomnia.App` | WPF `WinExe` | Tray icon, flyout, settings, notifications, and the `--recover` entry point for the recovery agent. |
| `Insomnia.Core.Tests` | `net10.0` | xUnit tests against fakes of every platform contract. |

## Feature mapping

| macOS | Windows |
| --- | --- |
| `sudo pmset -a disablesleep 1` | Two parts. **Idle sleep:** `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` plus a `PowerCreateRequest` system-required request named "Insomnia session until HH:MM" (visible in `powercfg /requests`). Both die with the process, so neither needs a journal entry. **Lid close:** the lid-close action (`SUB_BUTTONS\LIDACTION`) of the active power scheme is set to Do nothing for AC and DC through `PowerWriteACValueIndex`/`PowerWriteDCValueIndex` and re-applied with `PowerSetActiveScheme`. This is the persistent change, so it is journaled (`LidPolicyEntry`) before it is written. Neither needs administrator rights. |
| sudoers rule | Not needed. Nothing runs elevated. |
| `AppleClamshellState` IOKit notification | `RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE)` on a hidden message window. Windows sends the current state at registration. 2 s debounce, as upstream. |
| Display and keyboard backlight to 0 | Windows turns the internal panel off when the lid closes, whatever the lid action is. Insomnia additionally asks every display to power off (`SC_MONITORPOWER`, best effort, optional) and wakes them on open. There is no journal because nothing persists. No public API controls keyboard backlights (they are vendor-specific), so that row is omitted. |
| `SIGSTOP`/`SIGCONT` of app trees | `NtSuspendProcess`/`NtResumeProcess` on a handle opened by pid and verified by creation time. Suspend counts are per thread, so a resume undoes only Insomnia's own suspend. |
| Bundle ids | Executable image names (`slack.exe`), matched case-insensitively. "Dock apps" are processes owning a visible taskbar window. Processes under `%WINDIR%` are never frozen. |
| Electron helper trees (responsible pid) | Every process with the app's image name, plus their descendants by parent pid (a child must be created after its parent, which rules out reused parent pids). |
| Docker Desktop rule | Same two-probe flow with `docker ps -q`. Suspends `Docker Desktop.exe` and its `com.docker.*` backend processes. The WSL 2 VM belongs to the system and is not suspended; with no containers running it idles. |
| CoreAudio mute and restore per device | Core Audio `IMMDeviceEnumerator` and `IAudioEndpointVolume`. Device ids are endpoint ids. "Away" is `DEVICE_STATE_UNPLUGGED`, `NOTPRESENT` or `DISABLED`. `IMMNotificationClient` drives restore on reconnect. |
| Low Power Mode (`pmset -b lowpowermode`) | Power mode overlay "Best power efficiency" (`PowerSetActiveOverlayScheme`). Windows keeps one overlay per power source and the API sets the current one, so the journal has one `PowerModeEntry` per source. An entry for the inactive source waits until that source is active again. |
| Thermal state notification | No public thermal API exists for apps. The `Thermal Zone Information` performance counters (`% Passive Limit`) are polled every 30 s during a session: any zone below 100% for 2 samples is Serious, and any zone at or below 50% for 2 samples is Critical. Machines without the counters report no thermal state and the thermal rules never fire. |
| Battery IOPS notifications | `GUID_ACDC_POWER_SOURCE` and `GUID_BATTERY_PERCENTAGE_REMAINING` power-setting notifications. `GetSystemPowerStatus` for the reading, and the WinRT aggregate battery report for charge rate (watts). The re-read every 30 s while unreadable is kept. |
| App Nap opt-out (`NSAppSleepDisabled`) | Opt-in power-throttling (EcoQoS) exemption for running agent processes through `SetProcessInformation(ProcessPowerThrottling)`. The previous masks are journaled per process. The setting dies with the process, so no other app's persistent settings are ever touched. |
| Chromium occlusion throttling | Same check and relaunch. Command lines are read with `NtQueryInformationProcess(ProcessCommandLineInformation)`. Insomnia never closes the browser: it asks the user to quit it from its own menu (which prompts about unsaved pages and saves the windows), waits up to 2 minutes, then relaunches it with both flags and `--restore-last-session`. |
| `NWPathMonitor`, CoreWLAN, Keychain | `NetworkChange` events plus the Wi-Fi adapter's operational state for connectivity. WinRT `WiFiAdapter` for the scan and join, after `WiFiAdapter.RequestAccessAsync`, because Windows 11 requires location access before scans return SSIDs. The password is passed as a `PasswordCredential`, never in process arguments. Windows saves the hotspot as a known network when it joins. Secrets go to Credential Manager (`CredWriteW`, generic credential `Insomnia/hotspot/<SSID>`, per user, protected by DPAPI). |
| tmux nudge | Same marking rule (`@insomnia-nudge on`). tmux is reached through a configurable command, by default `wsl.exe -e tmux`. |
| launchd backstop with codesign pinning | Per-user Scheduled Task `\Insomnia\Recovery` that runs `Insomnia.exe --recover` at logon and every minute, hidden, with no window. It is registered with `schtasks.exe /Create /XML` and needs no administrator rights. The recovery agent is the same binary as the app, so it restores everything the app can, audio included. No code-signature pinning: the install folder and the task are per-user, like upstream's LaunchAgent plist, and a process running as the user can edit them (see Threat model). |
| `SMAppService` login item | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Insomnia`. Settings shows what Windows has on file, including "turned off in Task Manager" (`StartupApproved\Run`). |
| `UNUserNotificationCenter` | Windows toast notifications through `NotifyIcon` balloon tips. Windows 10 and 11 render them as toasts attributed to Insomnia, with no AUMID registration needed. |
| Menu bar pills and countdown | Tray icon (an eye that is open during a session) plus a flyout anchored above the tray: Days / Hours / Minutes fields, the live countdown, extend, and hold to end. Right-click opens the menu: status lines, warnings and their actions, Settings, Quit. The tooltip shows the remaining time. |

## Session lifecycle

**Start.** The steps run in order:

1. Refuse when the journal is unreadable or a session file was set aside but
   is still present.
2. Arm the recovery agent and refuse when it cannot be armed.
3. Write `session.json`.
4. For the active scheme, journal `LidPolicyEntry(previous AC/DC)` and only
   then write Do nothing.
5. Acquire keep-awake.
6. With the throttling exemption on, journal each agent process's previous
   masks and exempt it.

A failure in steps 4 to 6 undoes the start from the journal, like an end.

**Active scheme changes** during a session: the new scheme gets its own
journaled entry before its lid action is changed. Undo restores every
journaled scheme.

**End** (timer, user, quit, floors, thermal, recovery). The order is chosen
so the machine cannot sleep halfway through an undo:

1. Wake displays.
2. Resume frozen processes and Docker.
3. Restore audio.
4. Restore throttling.
5. Restore the power mode.
6. Restore the lid policy.
7. Delete `session.json`.
8. Release keep-awake.

Failed undos stay journaled, the end is reported incomplete, the app retries
every 30 s, and the recovery agent retries every minute.

**Lid close** (debounced, session active):

1. Mute: journal the default output's id, name, volume and mute state as its
   own entry, unless that device already has one, then mute it.
2. Freeze: journal each process (unconfirmed), suspend it, and mark it
   confirmed.
3. Run the Docker rule.
4. Evaluate the floor rules with the lid closed.
5. Request displays off.
6. Stop the countdown redraw.

**Lid open** reverses these: wake the displays, resume the processes, restore
the audio, re-evaluate the floors, and restart the redraw.

Each lid event is numbered as it arrives. An open makes every earlier close
stale and cancels a Docker probe in flight. The probe's late answer is
ignored.

**Reconcile at launch:**

- **Session missing, expired or unreadable:** restore the journal. An
  unreadable session file is renamed to `session.json.unreadable-<UTC>`.
- **Session valid:** arm the agent, re-apply the lid policy and keep-awake,
  and restore any lid-close actions if the lid is open. If the lid reads
  closed, the redraw stays stopped and no close actions run (no close event
  arrives for a lid that was already closed).

**Recovery agent** (`--recover`, every minute):

- Takes the recovery lock and exits quietly when it is busy.
- **Session missing, expired or unreadable:** restores the journal, deletes
  the session file, and notifies when it restored something.
- **Session valid:** does nothing; the deadline is honored.

**Journal invariants:**

- A lid action of Do nothing stays only while `session.json` exists with a
  future deadline, or while its undo failed and is being retried.
- An undo writes a value back only while the current value is still the one
  Insomnia applied (lid action, power mode overlay, mute). A value the user
  changed meanwhile is theirs, and the entry is cleared without a write.
- A process is resumed only when its pid and creation time match. An
  unconfirmed entry is resumed only when every thread reads suspended.
- Every transaction (start, end, lid event, restore, recovery) holds the
  recovery lock: an exclusive handle on `%LOCALAPPDATA%\Insomnia\recovery.lock`
  that Windows releases when the process dies.

## Files

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Insomnia\Insomnia.exe` | App and recovery agent |
| `%LOCALAPPDATA%\Insomnia\config.json` | Settings |
| `%LOCALAPPDATA%\Insomnia\session.json` | Active session (start, end, extensions) |
| `%LOCALAPPDATA%\Insomnia\state.json` | Journal |
| `%LOCALAPPDATA%\Insomnia\recovery.lock` | Recovery lock |
| `%LOCALAPPDATA%\Insomnia\Logs\insomnia.log`, `handoffs.log` | Logs, 1 MiB each, one older copy kept as `.1` |
| Task Scheduler `\Insomnia\Recovery` | Recovery agent |
| `HKCU\...\Run\Insomnia` | Launch at login (only when on) |
| Credential Manager `Insomnia/hotspot/<SSID>` | Hotspot password |

`INSOMNIA_HOME` overrides the data folder for tests, `--demo` and one-off
`--recover` runs. The tray app refuses to run sessions with it set: the
recovery task always recovers the default folder, and the lid action and
power mode are machine-wide, so exactly one journal may own them.

Writes are atomic: the content goes to a temporary file in the same folder,
which is flushed to disk and then moved over the target with
`File.Move(overwrite: true)` (`MoveFileEx` with `MOVEFILE_REPLACE_EXISTING`).

## Threat model

Insomnia runs as the user, with no elevation. Anything else running as the
user can edit the install folder, the scheduled task, and the journal, and
can change the lid action itself. Insomnia guards against accidents and
crashes, not against a hostile process running as the user. That is the same
boundary the upstream README draws for ad-hoc builds.

## Hardware validation still required

The engine logic is unit tested. The behaviors below depend on hardware and
firmware and must be checked on a real laptop, supervised, on a ventilated
surface, before any release claims them:

- **Modern Standby (S0ix) laptops:** check that a session with the lid closed
  keeps network-bound work running. Windows can still enter Modern Standby
  when the display turns off. A system-required power request is documented
  to prevent idle-initiated standby, but firmware varies. A display-off from
  `SC_MONITORPOWER` is not idle-initiated: Windows logs it as standby entry
  reason 3 (Kernel-Power 506) and sleeps about 5 s later despite the
  request, so the displays-off lid action is skipped on Modern Standby.
- **Lid-close action:** check that Do nothing applies immediately after
  `PowerSetActiveScheme` and is restored at end, quit, crash, and reboot.
- **Freezing:** check that Slack and Discord resume intact (no relaunch,
  connection restored) after a long suspend.
- **Thermal:** check whether the thermal zone counters respond on the target
  machine.
- **Hotspot:** check the hotspot join, including the location permission
  prompt.
