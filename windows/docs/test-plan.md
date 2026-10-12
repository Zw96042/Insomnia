# Manual test plan

Run this under supervision, on a ventilated surface, using disposable work,
before claiming hardware validation. It is a plan, not evidence: record each
result with the date, machine, Windows build and Insomnia version.

Useful commands while testing (PowerShell):

```powershell
powercfg /requests                                    # Insomnia's SYSTEM request during a session
powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION      # current AC/DC lid-close action indexes
Get-Content $env:LOCALAPPDATA\Insomnia\state.json     # the journal
Get-Content $env:LOCALAPPDATA\Insomnia\Logs\insomnia.log -Tail 40
schtasks /Query /TN "\Insomnia\" /FO LIST /V           # the recovery task
```

1. **First launch.**
   1. Install with `scripts\install.ps1`.
   2. Check that the eye is in the notification area (or its overflow) and
      that the task appears in Task Scheduler under `\Insomnia\`.
2. **Stays awake with the lid closed.**
   1. Start a 30-minute session.
   2. Check that `powercfg /requests` lists Insomnia under SYSTEM, and that
      `LIDACTION` reads 0 for both AC and DC.
   3. Close the lid and wait 5 minutes while pinging the laptop from
      another device, or running a long download.
   4. Open the lid. The session is still running.
   5. Repeat on battery. On a **Modern Standby** laptop, note whether
      network work keeps running with the lid closed.
3. **Restores.**
   1. Hold to end. `LIDACTION` reads the original values,
      `powercfg /requests` no longer lists Insomnia, and `state.json` is
      gone.
   2. Repeat with Quit.
   3. Repeat with the timer running out. A notification appears, and a
      "5 minutes left" reminder appears beforehand.
4. **Recovery agent.**
   1. Start a 2-minute session and end `Insomnia.exe` in Task Manager.
   2. Within a minute of the deadline, check that the lid action is
      restored and a "Sleep settings restored" notification appears.
   3. Start a long session, restart Windows, and sign in. The agent leaves
      the lid action at Do nothing until the deadline.
   4. Make `state.json` unreadable (invalid JSON). The agent logs it and
      changes nothing.
5. **Freeze.**
   1. Put Slack and Discord on the freeze list and close the lid.
   2. In Task Manager's Details tab, their processes, helpers included,
      show Suspended.
   3. Open the lid. They run, reconnect, and were not relaunched.
6. **Docker rule.** Turn the rule on.
   1. With no containers, close the lid. Docker Desktop's processes are
      suspended, and `insomnia.log` has "first check found no running
      container" and "second check found no running container".
   2. With one container running, Docker is untouched, and the log has
      "first check found containers running".
7. **Mute.**
   1. Set the volume to 60% and close the lid. The output is muted.
   2. Open the lid. Volume is back at 60% and unmuted.
   3. With a USB or Bluetooth headset as the output, close the lid,
      disconnect the headset, open the lid, and end the session. The
      notification and menu name the headset as still muted.
   4. Reconnect it. Its volume comes back and the menu line goes.
8. **Power saving.**
   1. Close the lid. Settings > System > Power shows Best power
      efficiency.
   2. Open the lid. The previous mode is back.
   3. Set the power-saving floor above the current charge while on
      battery: power saving turns on. Plug in: it turns off.
9. **Floors.** Set the end floor above the current charge, on battery. The
   session ends with "Battery floor reached".
10. **Thermal.**
    1. Check `insomnia.log` for "watching N thermal zone(s)" or "thermal
       rules are off".
    2. Do not overheat the machine on purpose. If the counters exist,
       observe them under a sustained CPU load with `Get-Counter
       "\Thermal Zone Information(*)\% Passive Limit"`.
11. **Browser throttling.** With Chrome running normally, start a session.
    1. The menu offers "Relaunch Google Chrome unthrottled".
    2. Confirm. Chrome reopens its windows, and `chrome://version` shows
       both flags.
    3. With the lid closed and Playwright attached to headed Chrome,
       compare `document.visibilityState` and `setInterval` drift with and
       without the flags.
12. **Hotspot and location.**
    1. Save a hotspot in Settings. The Windows location prompt appears.
    2. During a session, turn the router off. Within about 10 s Insomnia
       joins the hotspot, and `handoffs.log` records the gap.
13. **Nudge.**
    1. In WSL, mark a disposable pane with
       `tmux set-option -p -t <target> @insomnia-nudge on`.
    2. Force an outage longer than the threshold. That pane receives
       `continue` with no Enter, and an unmarked listed pane receives
       nothing.
14. **Throttling exemption.** Turn on "Turn off power throttling for agent
    apps", start a session, and check `state.json` for
    `throttleOverrides`. End: the entries are gone.
15. **Meeting apps.**
    1. With freeze-all on, a Teams or Zoom call running, and a dictation
       app open, close the lid.
    2. None of their processes is suspended, and the call continues.
16. **Upgrade and uninstall.**
    1. Run `install.ps1` again during a session. It quits Insomnia (ending
       the session) and installs.
    2. Run `uninstall.ps1`. The task, the startup entry and the app are
       gone.
    3. With a saved volume for an unplugged headset, `-Purge` keeps
       `state.json`.
