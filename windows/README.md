<h1 align="center">Insomnia for Windows</h1>

<!-- The Windows port lives in windows/ beside the macOS app; the repository README covers macOS. -->

<p align="center">
  <strong>Keep your PC awake. Give the session an end time.</strong>
</p>

<p align="center">
  A Windows tray app for timed awake sessions, ported from
  <a href="https://github.com/krishhgg/Insomnia">Insomnia for macOS</a>.
  Pick a duration for your long-running work, choose what happens when the
  lid closes, and see when recovery needs your attention.
</p>

> **Use a stable, well-ventilated surface, not a closed bag.** Insomnia is
> experimental software. Recovery can fail; a running timer is not a safety
> guarantee. See [Hardware validation](#hardware-validation) for what has
> and has not been checked on real machines.

## Install

Requires **Windows 10 version 2004 (build 19041) or later**, on x64 or Arm64.
No administrator rights are needed.

1. Download `Insomnia-<version>-win-x64.zip` (or `-win-arm64.zip` on Arm
   PCs) and `SHA256SUMS` from the
   [latest Windows release](https://github.com/Zw96042/Insomnia/releases/tag/windows-v0.1.0).
   Releases are tagged `windows-v<version>` and marked Pre-release.
2. Verify the download in PowerShell. `gh` is the
   [GitHub CLI](https://cli.github.com).

   ```powershell
   (Get-FileHash .\Insomnia-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()
   Get-Content .\SHA256SUMS
   gh attestation verify .\Insomnia-<version>-win-x64.zip -R Zw96042/Insomnia `
     --signer-workflow Zw96042/Insomnia/.github/workflows/windows.yml `
     --source-ref refs/tags/windows-v<version>
   ```

   The two hashes must
   match. The `gh` command checks that the repository's Windows workflow
   built this exact zip for that tag. Windows releases are tagged
   `windows-v<version>`; plain `v<version>` tags are the macOS app.

3. Unzip it and run the installer that comes in the zip:

   ```powershell
   Expand-Archive .\Insomnia-<version>-win-x64.zip -DestinationPath .\Insomnia
   cd .\Insomnia
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -AllowUnverifiedOrigin
   ```

   Releases are not code-signed. The installer can check that the build
   matches your PC, but not who made it, so it refuses to install a prebuilt
   build without `-AllowUnverifiedOrigin`. That flag says you ran the checks
   in step 2. The installer then unblocks the installed files, so Windows
   SmartScreen does not stop the recovery agent the first time it runs.

### Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/krishhgg/Insomnia.git
cd Insomnia\windows
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The installer builds Insomnia for your PC's architecture with
`dotnet publish`, then installs it the same way as a release. Builds are
self-contained: `Insomnia.exe` carries the .NET runtime, and WPF's native
libraries sit beside it, so no .NET install is needed to run it. The cost
is size, about 170 MB installed and 70 MB zipped.

<details>
<summary><strong>Exactly what gets installed</strong></summary>

| Location | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Insomnia\` | `Insomnia.exe`, WPF's native libraries, and `uninstall.ps1`. Run with `--recover`, `Insomnia.exe` is also the recovery agent. |
| `%LOCALAPPDATA%\Insomnia\` | `config.json` (settings), `session.json` (the running session), `state.json` (the journal of changes to undo), `recovery.lock` |
| `%LOCALAPPDATA%\Insomnia\Logs\` | `insomnia.log` and `handoffs.log`, each capped at 1 MiB with one older copy kept as `.1` |
| Task Scheduler: `\Insomnia\Recovery-<your SID>` | Per-user recovery agent. It runs `Insomnia.exe --recover` at sign-in and every minute, hidden, with least privilege. |
| Start menu: `Insomnia` | Shortcut |
| `HKCU\...\CurrentVersion\Run\Insomnia` | Only when **Launch at login** is on |
| Credential Manager: `Insomnia/hotspot/<SSID>` | Only when you save a hotspot password |

Insomnia changes these Windows settings only during a session, and journals
each one in `state.json` before changing it:

- **Lid-close action** of the active power plan: set to **Do nothing** for
  plugged in and on battery, so closing the lid does not sleep the PC.
  Without this, Windows sleeps on lid close no matter what an app asks.
- **Power mode:** set to **Best power efficiency** while the lid is closed
  or a battery or thermal rule asks for it.
- **Paused apps:** suspended while the lid is closed.
- **Default output's mute state:** muted while the lid is closed.

Undo writes a value back only while it still reads what Insomnia set. A
setting you changed during the session is yours, and Insomnia leaves it.

Nothing runs elevated. Anything running as you can edit the install folder,
the scheduled task and the journal, and could change the lid-close action
itself. Insomnia guards against crashes and accidents, not against a hostile
program running as your user.

</details>

## Using it

1. **Start:** click the eye in the notification area, type Days / Hours /
   Minutes, and press Enter. With every field empty, Enter starts the
   default preset (4 hours). The chips below the fields fill them in.
2. **Extend:** while a session runs, open the flyout and enter more time.
3. **End early:** press and hold the end control in the flyout.
4. **Inspect or configure:** right-click the eye for status, recovery
   warnings, **Settings**, and **Quit Insomnia**. Quitting ends the session
   first.

Hover the eye for the time left. You do not need to close the lid to use a
session. Opening the lid does not end it, and a display that is off is not
the same as a PC that is asleep.

## What happens when the lid closes

During a session, a lid close runs these actions once the lid has stayed
closed for 2 seconds. Each change is journaled first, and opening the lid
undoes them. **The timer keeps counting while the lid is closed.**

- **Pause apps (default on).** Slack, WhatsApp and Discord are on the freeze
  list, and are suspended with every helper process they started. With
  **Freeze every other app** on (default off), every app with a taskbar
  window is also suspended, except:
  - agent apps;
  - Windows components;
  - Docker Desktop;
  - a built-in protected set: editors, terminals, browsers, VPN, local
    model runtimes, password managers, local databases, every JetBrains IDE,
    and meeting, recording and dictation apps (Zoom, Teams, Webex, Wispr
    Flow, Granola, Otter, OBS, Loom). Freezing a meeting app ends the call.

  Settings shows a "Would freeze now" line. Only processes Insomnia
  suspended are resumed. A process somebody else also suspended stays
  suspended, because Windows counts suspensions per thread.
- **Docker rule (default off).** When no container is running, Docker
  Desktop's own processes are suspended. Docker is asked twice, the second
  time right before the suspend, over Docker Desktop's local pipe. A busy
  answer, a failed check, or the lid opening during either check leaves
  Docker running. Docker's WSL 2 VM belongs to Windows and is not suspended;
  with no containers it idles.
- **Mute (default on).** The default output is muted, and its volume and
  mute state are saved per device. Opening the lid restores each device
  that is still muted. A device you unmuted meanwhile is left as it is. A
  device that is unplugged keeps its saved volume until it comes back, and
  the menu offers **Stop waiting for \<device\>**.
- **Displays off (default on).** Windows already turns the built-in panel
  off when the lid closes. Insomnia also asks external displays to power
  off, and wakes them on open.
- **Power saving (default on).** **Best power efficiency**, so a closed
  laptop does not keep running at full speed.

Many laptops disconnect the built-in microphone when the lid closes, so a
meeting with the lid closed needs a headset or an external mic.

Keyboard backlights have no standard Windows API, so Insomnia leaves them
to your laptop's firmware. Most turn them off with the lid.

## Battery, heat and network

| Condition | Action |
| --- | --- |
| Battery below **40%** on battery | Power saving on |
| Battery below **10%** on battery (0 turns this off) | End the session |
| Battery present but unreadable twice in a row, on battery | End the session |
| Thermal throttling for two readings in a row | Power saving on |
| Heavy thermal throttling (passive limit at or below 50%) for two readings in a row | End the session |
| Lid closed | Power saving on (no notification) |

Power saving switches off again when no rule wants it. Windows has no
thermal API for apps, so the thermal rules read the
`Thermal Zone Information` performance counters every 30 seconds. Machines
without them have no thermal rules. These rules need the app running.

**Network failover.** Set a hotspot SSID and password in Settings >
Network. During a session, if Wi-Fi has been down for 5 seconds, Insomnia
scans for that hotspot and joins it, retrying after 5, 10, 20 and 30
seconds, then every 30 seconds. Windows 11 requires location access for
Wi-Fi scans, so Insomnia asks for it when you save a hotspot or start a
session with one set. The password is kept in Credential Manager and never
appears in process arguments. Windows remembers the hotspot as a known
network once joined.

Every outage is logged to `handoffs.log`, and the menu shows the last gap.
After an outage longer than the nudge threshold (90 seconds), Insomnia
types `continue` into tmux panes you marked, inside WSL by default:

```sh
tmux set-option -p -t <target> @insomnia-nudge on
```

Add the targets in Settings > Network. Unmarked panes are skipped. Enter is
pressed only if you turn that on.

**Agent apps.** Apps on the agent list are never paused. With **Turn off
power throttling for agent apps** on (default off), Insomnia exempts their
running processes from Windows power throttling (EcoQoS) for the session,
and puts each process's previous setting back at the end.

**Browsers.** Chromium-based browsers throttle windows Windows reports as
hidden, which can include every window once the lid is closed. When a
browser runs without `--disable-backgrounding-occluded-windows` and
`--disable-renderer-backgrounding`, the menu offers to relaunch it with
both flags and the same profile. Insomnia never closes the browser itself:
it asks you to quit it from its own menu, which warns about unsaved pages
and saves your windows, then starts it with both flags and
`--restore-last-session` so those windows come back. Private windows are
not restored. If you don't quit within two minutes, nothing happens.

## How recovery works

Every change is written to `state.json` before it is made, and undone from
that file, never from memory:

- **Session end** (timer, hold to end, quit, a battery or thermal rule):
  undoes everything, removes `session.json`, and releases the keep-awake
  request.
- **Launch:** an expired or unreadable session is restored at once. A valid
  one is resumed.
- **Recovery agent:** every minute and at sign-in, runs the same restore
  code once a session's end time has passed. It covers a crash, a forced
  quit, or a restart. It honors a session that has not ended yet.

An unreadable `session.json` is never deleted: it is renamed to
`session.json.unreadable-<time>` and treated as ended. One that cannot be
opened at all (held by another program) is left alone and checked again
next minute, since it may hold a live session.

Windows counts suspensions, so a resume is never replayed. Insomnia
records each resume before it makes it. If one was interrupted and the app
still reads paused, the menu shows **Resume \<app\>** and leaves the
choice to you: if another program also paused that app, resuming releases
its pause too. A restore that fails
stays journaled and is retried every 30 seconds by the app and every minute
by the agent, and the menu says what is left. Quitting is refused only when
something is left and the recovery agent is not registered.

The keep-awake request itself dies with the process. A crashed Insomnia
cannot keep the PC awake, only leave the lid-close action at Do nothing
until the agent's next run, within a minute.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\Insomnia\uninstall.ps1"
```

The installer puts `uninstall.ps1` next to the app. It quits Insomnia,
runs the recovery agent once, and stops
without removing anything if something could not be restored. It then
removes the scheduled task, the startup entry, the shortcut and the app.
`-Purge` also deletes settings, logs and saved hotspot passwords. Even
then, a journal holding saved volumes for audio devices that are not
connected is kept, so a later install can restore them.

## Hardware validation

The session engine (journal, rules, freeze planning, lid sequencing,
recovery) is covered by unit tests that run on any OS. The Windows behavior
below depends on hardware and firmware. Check it on a real laptop,
supervised, on a ventilated surface, before relying on it:

- **Lid close:** the PC stays up with the lid closed and the lid-close
  action is restored after end, quit, crash and restart. On **Modern
  Standby** laptops, check that network-bound work keeps running while the
  display is off with the lid closed.
- **Freezing:** Slack and Discord resume intact after a long suspend.
- **Thermal:** the thermal zone counters respond on the machine.
- **Hotspot:** the join works, including the location prompt.

## Development

```powershell
dotnet test tests/Insomnia.Core.Tests     # engine tests; also run on macOS and Linux
dotnet build Insomnia.sln -c Release
dotnet run --project src/Insomnia.App -- --demo
```

`--demo` runs the full interface against an in-memory engine that changes
nothing on your PC. `INSOMNIA_HOME` points tests and one-off `--recover`
runs at another data folder. The tray app refuses to run sessions with it
set, because the recovery task always recovers the default folder and the
settings a session changes are machine-wide.
[docs/design.md](docs/design.md) maps each macOS mechanism to its Windows
counterpart and records the session lifecycle and journal rules.

| Project | Role |
| --- | --- |
| `src/Insomnia.Core` | Models, journal store, rules, the session engine and the recovery runner. Platform-neutral. |
| `src/Insomnia.Windows` | Windows implementations: power and lid APIs, process suspend, Core Audio, Wi-Fi, Task Scheduler, Credential Manager. |
| `src/Insomnia.App` | Tray icon, flyout, settings, notifications, and the command-line entry points. |
| `tests/Insomnia.Core.Tests` | xUnit tests against fakes of every Windows service. |

## Credits

Insomnia was designed and built for macOS by
[Krish Garg](https://github.com/krishhgg/Insomnia). This port follows its
spec and keeps its MIT license.
