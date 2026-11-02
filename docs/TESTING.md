# Testing on real hardware

The automated tests check the bridge's logic with no controller attached. They can't tell whether a real controller, Windows or Steam behaves as expected. This checklist covers that, in order, so that a problem early on doesn't waste later steps.

**If a step fails:** note the step number, click **Open log** (`%LOCALAPPDATA%\Switch2Bridge\bridge.log`) and copy the lines around the problem. For input problems, also check `%LOCALAPPDATA%\Switch2Bridge\input-log`.

Sections marked *Needs Joy-Cons* or similar can be skipped if you don't have the hardware.

## 1. Fresh start (2 minutes)

- [ ] 1.1 Quit every running copy from the tray. Check Task Manager shows no `Switch2Bridge` process.
- [ ] 1.2 Delete the old `release-0.5` folder in the repo.
- [ ] 1.3 Run the newest `artifacts\release\Switch2Bridge-v*.exe` and approve the admin prompt. **Expect:** the tray icon appears, and the exe is copied to `%LOCALAPPDATA%\Programs\Switch2Bridge`.
- [ ] 1.4 **Expect:** "Switch 2 Native Bridge" is in the Start Menu and in Windows' Installed apps.
- [ ] 1.5 Open the status window from the tray. **Expect:** Windows 11 styling, the right version in Settings, and no setup warnings that don't apply.

## 2. Connecting

- [ ] 2.1 Press a button on the Pro Controller. **Expect:** a card appears within a few seconds and a player light comes on.
- [ ] 2.2 **Expect on the card:** a battery level from 0 to 9 with voltage. If it shows low/medium/high instead, the level read failed, so find `battery level` in the log.
- [ ] 2.3 **Expect:** a firmware version and "Headset audio channel" yes or no.
- [ ] 2.4 **Expect:** reports per second close to before (you saw 68), and a low stall count.
- [ ] 2.5 Run for 2 minutes with the controller untouched. **Expect:** no repeated `no reports for 5 s` in the log and no disconnecting. This checks the new dead-link detection isn't too eager.

**Write down:** the firmware version, and the `link interval` and `connected ... in N ms` lines from the log.

## 3. Inputs, without Steam

- [ ] 3.1 Click **Test inputs**. Press every button. **Expect:** each lights up and gets a tick, and the count reaches 21 of 21.
- [ ] 3.2 Move both sticks to every edge. **Expect:** the dots follow, the right way round and not reversed.
- [ ] 3.3 Tilt the controller. **Expect:** the gyro bars move.
- [ ] 3.4 Press Home with the window open. **Expect:** Big Picture does **not** open.
- [ ] 3.5 Close the window, press Home. **Expect:** Steam reacts again. Check nothing's stuck: no held button in Steam.

## 4. In Steam and a game

- [ ] 4.1 In Steam Settings → Controller. **Expect:** the controller is listed. Note what it's called.
- [ ] 4.2 Use Steam's controller test: buttons, GL, GR, sticks, gyro. **Expect:** all respond.
- [ ] 4.3 Change a binding and the gyro sensitivity in Steam Input in a game. **Expect:** they take effect. If gyro sensitivity doesn't, turn off the game's own gyro or controller support and try again.
- [ ] 4.4 Play for 10 minutes. **Expect:** no drops or stuck inputs. Afterwards, look through the input log for that time.
- [ ] 4.5 Add a non-Steam game to Steam and try it. **Expect:** input works. Separately, try a game outside Steam. **Expect:** no input (known limit).

## 5. Rumble

- [ ] 5.1 Click **Test rumble**. **Expect:** you feel a thump, then a click.
- [ ] 5.2 Set Rumble strength to 25% and test again. **Expect:** clearly weaker. Set it back.
- [ ] 5.3 Trigger rumble in a game. **Expect:** it works and stops when it should. The card shows "Rumble".

## 6. Lights, names and players

- [ ] 6.1 Connect in Steam or a game that sets a player number. **Expect:** the light matches, and the log shows `Steam set the lights`. If nothing changes, Steam may not be setting them; note that.
- [ ] 6.2 Click **Rename**, name the controller. **Expect:** the card and tray menu show the name.
- [ ] 6.3 Quit and restart the bridge, connect. **Expect:** same name and player number.

## 7. Reconnecting and power

- [ ] 7.1 Hold the controller's power or Sync button to turn it off. **Expect:** the card shows it's off, and Steam keeps the device.
- [ ] 7.2 Press a button. **Expect:** it returns as the same player. Compare the `connected ... in N ms` log line with 2. **Expect:** faster.
- [ ] 7.3 Tray → **Turn off controllers**. **Expect:** the controller turns off. The log shows `sent power-off`. If it stays on with lights flashing, note that.
- [ ] 7.4 Set "After no input for" to 5 minutes, leave it. **Expect:** it turns off at 5 minutes. Set it back.
- [ ] 7.5 Put the PC to sleep. **Expect:** the controller turns off. Wake the PC, then press Home. **Expect:** it reconnects without restarting the bridge.
- [ ] 7.6 Turn Bluetooth off in Windows. **Expect:** a warning in the window. Turn it on. **Expect:** the warning clears, and the controller reconnects when you press a button.
- [ ] 7.7 Quit the bridge. **Expect:** the controller turns off.

## 8. Windows integration

- [ ] 8.1 Sign out and back in. **Expect:** after about 10 seconds the bridge is running with no admin prompt. Right-click the tray icon to confirm the version.
- [ ] 8.2 Untick **Start with Windows**, sign out and in. **Expect:** it doesn't start. Tick it again.
- [ ] 8.3 Close Steam. **Expect:** a warning within 15 seconds. Open it. **Expect:** the warning clears.
- [ ] 8.4 Click **Create diagnostics report**, open the zip. **Expect:** no Bluetooth addresses or your user name in any file.

## 9. Recording and replay tests

- [ ] 9.1 Click **Record**, press every button and move both sticks, then **Stop recording**. **Expect:** Steam gets nothing during it, and a file in `recordings`.
- [ ] 9.2 Copy it to `tests\recordings\pro-all-inputs.s2rec`, then follow [its README](../tests/recordings/README.md) to save the expected events. Check they match what you pressed.
- [ ] 9.3 Run `tools\build.ps1`. **Expect:** it reports `PASS pro-all-inputs.s2rec`.

## 10. Headset audio test (*needs a headset with a mic*)

- [ ] 10.1 Plug in the headset. Settings → **Run audio test**, follow the prompts. **Expect:** about 22 seconds with the controller's input paused, a result message, and the results saying normal input came back. Check the controller works straight afterwards, and that Steam saw nothing held down.
- [ ] 10.2 Send me the `.txt` from **Open results** (not the `.bin`, which may contain your voice). This decides whether headphone audio is worth pursuing.

- [ ] 10.3 *Headphones needed, but don't wear them.* Turn the controller's volume low and hold them near your ear. Settings → **Play test sounds**, confirm. Click where you hear each sound as soon as you hear it, and **Stop** if it's unpleasant. **Expect:** under 2 minutes, then a result, and the controller works again. Send me the newest `sounds-*.txt` from **Open results**, even if you heard nothing.

## 11. Joy-Cons (*needs Joy-Cons*)

- [ ] 11.1 Wake both within a few seconds. **Expect:** one "Joy-Con pair" card, both battery levels and firmware versions.
- [ ] 11.2 Test inputs: everything works, and both sticks are on the right sides. Try gyro and Test rumble.
- [ ] 11.3 Turn off one. **Expect:** its stick and buttons release, the other keeps working. Wake it. **Expect:** it rejoins the same card.
- [ ] 11.4 Quit the bridge, turn both off, restart, and wake only the right one. Wait 6 seconds. **Expect:** a "Joy-Con (R)" card with a **Hold sideways** box.
- [ ] 11.5 Tick it, then Test inputs held sideways with the stick on your left. **Expect:** face buttons match their positions (top is X, right is A, bottom is B, left is Y). SL is L, SR is R, and the stick moves correctly as held.
- [ ] 11.6 Check gyro in a game while sideways. **Expect:** turning and tilting go the right way. This is the likeliest part of sideways to need adjusting.
- [ ] 11.7 Repeat 11.4 to 11.6 with the left Joy-Con.
- [ ] 11.8 Set both as sideways and wake both. **Expect:** two separate players.

## 12. Several controllers (*needs a second controller*)

- [ ] 12.1 Connect two. **Expect:** two cards, two players with different lights, and each one's inputs only affect its own card.
- [ ] 12.2 Restart the bridge and connect them in the opposite order. **Expect:** each keeps its player number.

## 13. Uninstall and reinstall (do this last)

- [ ] 13.1 Quit the bridge. Windows Installed apps → Switch 2 Native Bridge → Uninstall. Answer **No** to drivers and data. **Expect:** the app folder, Start Menu entry and startup task go; settings stay.
- [ ] 13.2 Run the exe again. **Expect:** it installs and connects as before, with your settings.
- [ ] 13.3 Optional, full removal: uninstall answering **Yes** to both. **Expect:** no HIDMaestro devices in Device Manager, no `%LOCALAPPDATA%\Switch2Bridge`. A restart may be needed. Reinstalling afterwards should reinstall the drivers.

## After testing

- [ ] Add each firmware version that passed to [`firmware-support.json`](../firmware-support.json), like `{ "controller": "Pro", "firmware": "2.0.0" }`.
- [ ] Update the status column in the README's feature checklist.
- [ ] Fix or list anything that failed, bump the version, and add patch notes.
- [ ] Commit, push and tag, such as `git tag v0.8.0` then `git push origin v0.8.0`. **Expect:** GitHub creates the release with the exe and `.sha256`.
- [ ] Download the release on your PC and check its checksum matches.
- [ ] Build the next version, tag it, and use **Update now** in the app to test the updater. **Expect:** the banner appears, it updates and restarts, controllers reconnect, the old exe is removed.
