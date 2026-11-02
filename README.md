# Switch 2 Native Bridge

Use Nintendo Switch 2 Pro Controllers and Joy-Cons **over Bluetooth** on Windows, with **Steam treating them as wired Switch 2 Pro Controllers**. Steam Input handles button mapping, gyro, back buttons (GL/GR), rumble and per-game configs.

Steam only supports the Switch 2 Pro Controller over USB. On Bluetooth, the existing tools pretend to be an Xbox or PS4 controller and handle mapping and gyro themselves. This bridge doesn't. It reads the controller over Bluetooth and passes its raw data to a virtual *wired* Pro Controller 2, so Steam does the rest.

> **Early software.** Most features below are built but haven't been tried on a real controller yet. The feature checklist says which is which.

| Light | Dark |
| --- | --- |
| ![Status window, light](design/preview/status-light.png) | ![Status window, dark](design/preview/status-dark.png) |

## Feature checklist

**Confirmed** means it has worked on a real controller. **Built** means the code is finished and passes the automated checks, but no one has confirmed it on real hardware.

| Feature | Status |
| --- | --- |
| Pro Controller 2 over Bluetooth, shown to Steam as wired | Confirmed |
| All buttons including GL and GR, both sticks and gyro, in a Steam game | Confirmed on an earlier version |
| Battery voltage reading | Confirmed |
| Controller's own stick and motion calibration given to Steam | Built |
| Rumble from Steam, test button and strength slider | Built |
| Battery level 0 to 9, charging state and low-battery warning | Built |
| Real battery voltage passed to Steam | Built, but Steam may not show it |
| Several controllers at once, each keeping its player number | Built |
| Left + right Joy-Cons merged into one controller | Built |
| Single Joy-Con held sideways as its own controller | Built |
| Controller names | Built |
| Steam's player lights shown on the controller | Built |
| Fast reconnects, recovery after sleep or a dead link | Built |
| Turn off when idle, when the PC sleeps and when the bridge quits | Built |
| Bluetooth stats: reports per second, gaps, stalls | Confirmed reports per second; the rest is built |
| Test inputs window, with Steam input paused while it's open | Built |
| Input log and session recording, with recordings replayed as tests | Built |
| Firmware version display, tested list and local history | Built |
| Setup warnings and diagnostics report | Built |
| Tray app and Windows 11 status window | Confirmed on an earlier version |
| Start with Windows without an admin prompt | Built |
| Install location, Start Menu entry and Windows uninstall | Built |
| In-app updates and automatic GitHub releases | Built, waiting on a first release |
| Headset audio test (listen only) | Built |
| Audio through the headphone jack | **Not working**: see [Known limits](#known-limits) |
| Non-Steam games | **Not working**: see [Known limits](#known-limits) |
| NFC, magnetometer, waking the PC with the controller | **Not supported** |

## Requirements

- Windows 10/11 x64 with a Bluetooth adapter that supports Bluetooth Low Energy
- Steam
- Admin rights (one prompt each time you start it yourself; Start with Windows runs without a prompt)

## Install

1. Download `Switch2Bridge-v<version>.exe` from [Releases](../../releases), or build it (below). The exe isn't code-signed, so Windows SmartScreen will warn you.
2. If you've paired the controller in Windows' Bluetooth settings, **remove it there**. The bridge connects to it directly.
3. Close any other controller bridge app so they don't fight over the controller.
4. Run the exe and approve the admin prompt. It:
   - copies itself to `%LOCALAPPDATA%\Programs\Switch2Bridge` and runs from there, so you can delete the download;
   - adds a Start Menu entry and an entry in Windows' Installed apps;
   - installs HIDMaestro's virtual controller driver and its virtual USB driver (usbip-win2, Microsoft-signed), and adds HIDMaestro's self-signed driver certificate to the machine's trusted certificates;
   - on a first install, turns on **Start with Windows**, using a Task Scheduler logon task. Switch it off in the status window or tray menu.
5. **New controller:** hold its Sync button until the lights sweep. **Paired before:** press any button.

Left-click the tray icon for the status window, and right-click it for the menu. The log is at `%LOCALAPPDATA%\Switch2Bridge\bridge.log`.

### Uninstall

Quit the bridge from the tray, then uninstall **Switch 2 Native Bridge** from Windows' Installed apps. It removes the app, its startup task and its Start Menu entry, then asks whether to also remove the HIDMaestro drivers and your settings and logs.

## Using it

### Joy-Cons

Wake both within a few seconds and they join into one controller: left stick and buttons from the left, right stick, buttons and gyro from the right. To use one on its own sideways, connect only that one and tick **Hold sideways** on its card. It's remembered, so a Joy-Con set this way always starts as its own controller. SL and SR become L and R, the face buttons follow their new positions, and its stick becomes the left stick.

### Names and players

**Rename** on a controller card gives it a name, and each controller keeps its player number and lights between sessions. That number is the bridge's: Steam and games choose their own order. Lights change to match if Steam sets them.

### Rumble, battery and power

**Test rumble** plays a pattern directly on the controller, and the strength slider in Settings scales down Steam's rumble. The card shows battery level, voltage and charging. The controller turns off after 15 minutes without button or stick input by default, when the PC sleeps and when the bridge quits; each can be changed in Settings. Press Home to wake it.

### Debugging inputs

- **Test inputs** shows every button, both sticks and the gyro as the bridge receives them. If something lights up there but not in a game, check Steam or the game. Steam and games don't receive that controller's input while the window is open, so Home won't open Big Picture.
- **Input log:** `%LOCALAPPDATA%\Switch2Bridge\input-log` keeps the last 7 days of presses, releases, stick moves and Bluetooth gaps of 50 ms or more. Times are when reports reached the bridge, so it can't see the moment of a physical press or anything after Steam. You can turn it off in Settings.
- **Record** saves a session's raw reports to `%LOCALAPPDATA%\Switch2Bridge\recordings`. Steam and games don't receive that controller's input while it records. See [tests/recordings](tests/recordings/README.md) for turning one into a test.

### Reporting a problem

The status window warns about common setup problems. For anything else, **Create diagnostics report** in Settings saves a zip of versions, setup, status and recent logs, including recent inputs. Bluetooth addresses and your Windows user name are removed, and battery logs, recordings and audio captures are left out. Look through it, then attach it to a [bug report](../../issues/new/choose). Nothing is sent automatically.

## Firmware

The status window shows each controller's firmware version. The bridge can't update firmware: please use a Nintendo Switch for that.

- [`firmware-support.json`](firmware-support.json) lists versions confirmed working on a real controller, and is built into the app. It's empty so far. A version not on the list is untested, not unsupported. To report one, use the [firmware report form](../../issues/new/choose).
- `%LOCALAPPDATA%\Switch2Bridge\firmware-history.csv` notes each firmware version your PC has seen. It stays on your PC.

## How it works

```
Controller ──BLE GATT──▶ Switch2Bridge ──HIDMaestro──▶ virtual USB Switch 2 Pro Controller ──▶ Steam
```

- **Bluetooth side** (`SwitchController.cs`): connects with WinRT's Bluetooth LE APIs, runs the controller's GATT command protocol and receives its input reports.
- **USB side** (`VirtualPad.cs`): HIDMaestro's `switch2-pro-controller-composite` persona is a two-interface USB device that answers the bulk command protocol Steam uses to start a wired Pro Controller 2. A wired pad's input report 0x05 is the Bluetooth input report with a report ID added, so buttons, sticks and motion are copied across unchanged. The persona's flash image is replaced with the controller's own calibration.
- **Joy-Cons**: in vertical grip both use the Pro Controller's stick slots and motion axes, so merging is a matter of picking each side's bytes. `SidewaysJoyCon.cs` turns a lone Joy-Con's layout.

## Testing

- **Automated:** `tools\build.ps1` and GitHub Actions run the self-test and replay any recordings in [tests/recordings](tests/recordings/README.md). No controller, driver or admin needed. They check the bridge's logic, not how a real controller behaves.
- **On real hardware:** [docs/TESTING.md](docs/TESTING.md) is the checklist.

## Build

Requires the .NET 10 SDK.

```
powershell -NoProfile -File tools\build.ps1
```

This builds, runs the tests, and writes `artifacts\release\Switch2Bridge-v<version>.exe` with a `.sha256` file next to it. All build output lives under `artifacts\`.

To publish a release, set `<Version>` in `Switch2Bridge.csproj`, add its patch notes below, then push a matching tag such as `v0.8.0`. GitHub Actions runs the tests, then creates the release with the exe and its `.sha256` file, which the in-app updater needs.

`lib/HIDMaestro.Core.dll` is HIDMaestro v2.1.0 from its [official release](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v2.1.0) (release zip SHA-256 `7fe349f163864b8dfc36117af260d51bac94a2263b6cdfa719cb364a808397b4`).

## Known limits

- **Non-Steam games get no input.** Per HIDMaestro's notes, only Steam (and SDL with libusb) can read this virtual controller. Add a game to Steam to use it.
- **Headphone jack audio doesn't work.** The controller's Bluetooth audio format is undocumented, and another active project, [switch2-pro-controller-windows](https://github.com/jony25/switch2-pro-controller-windows), lists it as unsolved too. Working it out needs a capture of a real Switch 2 sending audio to the controller. **Run audio test** in Settings shows what your controller sends.
- **Input delay.** A Switch 2 talks to its controllers every 5 ms. Windows' limit is 7.5 ms, and one test PC settled on 15 ms (about 68 reports a second).
- **The exe isn't code-signed**, so SmartScreen warns about it. That needs a paid certificate.
- **Steam's label and battery.** Steam may call the device "Nintendo Switch Pro Controller". The bridge passes the real battery voltage to Steam, but SDL's Switch 2 driver doesn't read battery, so Steam may show none.
- **Sideways Joy-Con.** The virtual controller is still a Pro Controller, so Steam shows Pro Controller prompts, and the stick's calibration isn't turned with it. Motion axes follow switch2controllerpc and may need adjusting.
- **Power-off** sends the shutdown command a Switch 2 sends its controllers when it sleeps. It's documented over USB but unconfirmed over Bluetooth, so the bridge also drops the link.
- **Games with their own Switch controller support** may read gyro directly through SDL, so Steam's gyro settings don't apply there. Turn off the game's own gyro or controller support to let Steam handle it.
- **NFC and the magnetometer** aren't passed through, because HIDMaestro's persona doesn't emulate them.

## Patch notes

No version has been published as a GitHub Release yet. Dates are when each was built.

### 0.8.0 (2026-11-02)

- Faster reconnects: a controller already seen since the bridge started skips the full Bluetooth discovery, and connection time is logged.
- Recovery: sessions whose reports stop for 5 seconds end so the controller can reconnect, and listening restarts if Windows stops it.
- Steam's player lights now reach the controller.
- Start Menu entry, Windows Installed apps entry, and a full uninstall with optional driver and data removal.
- Single Joy-Con held sideways as its own controller, remembered per Joy-Con.
- A lone Joy-Con is now called Joy-Con (L) or (R), not a pair, and Test inputs lists only the buttons it has.

### 0.7.0 (2026-11-02)

- Steam input is paused while Test inputs is open or a controller records, so Home can't open Big Picture.
- Automatic GitHub releases and test runs, with bug and firmware report forms.
- Diagnostics report with addresses and user name removed.
- Installs to a fixed folder, and an older download starts the newer installed copy.
- Setup warnings for Bluetooth off, Steam closed, other controller apps and controllers paired in Windows.
- Controller names, and each controller keeps its player number.
- Update banner wording corrected: controllers reconnect after an update, rather than staying connected.

### 0.6.1 (2026-11-02)

- Battery level 0 to 9 and charging state from the controller itself.
- Listen-only headset audio test.
- Firmware version on each card, with a tested list, local history and a reminder to update on a Nintendo Switch.
- Test inputs window, session recording, continuous input log and recording replays in the build.

### 0.6.0 (2026-11-01)

- Battery log, percentage learned from each controller's history, and low-battery warning.
- Test rumble button and rumble strength slider.
- Bluetooth stats: average and worst gap, stalls per minute.
- Turn off when idle, when the PC sleeps and when the bridge quits.
- In-app updates from GitHub Releases.
- Settings section in the status window.

### 0.5.0 (2026-11-01)

- All build output moved to `artifacts\`, with `tools\build.ps1`.
- Battery shown as high, medium or low with voltage and charging, replacing a wrong percentage guess.
- Fast Bluetooth mode requested after connecting, with the real link speed and battery logged.
- Real battery voltage passed to Steam, a steadier report rate reading, and an app icon.
- Warning when an older copy is running, and clearer Bluetooth errors.

### 0.2.0 (2026-11-01)

- Several controllers, Joy-Con pairs, tray app, native Windows 11 status window and Start with Windows.

### 0.1.0 (2026-11-01)

- First working bridge: a Pro Controller 2 over Bluetooth appears to Steam as a wired controller, with buttons, sticks, gyro, rumble and the controller's own calibration passed through.

## Credits

- [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro) by hifihedgehog (MIT): the virtual wired Pro Controller 2 that makes native Steam support possible.
- [ndeadly/switch2_controller_research](https://github.com/ndeadly/switch2_controller_research): Switch 2 controller protocol documentation.
- [Nadeflore/switch2-controllers](https://github.com/Nadeflore/switch2-controllers): Switch 2 controller Bluetooth protocol reverse engineering.
- [TommyWabg/switch2-controllers-windows10-gyro](https://github.com/TommyWabg/switch2-controllers-windows10-gyro) and [CareyScott/switch2controllerpc](https://github.com/CareyScott/switch2controllerpc) (MIT): the Windows implementations this bridge's Bluetooth code and sideways layout follow.

Not affiliated with Nintendo or Valve. Nintendo Switch, Joy-Con and Steam are trademarks of their owners.

## License

MIT. See [LICENSE](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
