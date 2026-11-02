# Controller recordings

Real controller sessions the build replays with no controller attached.

To add one:

1. In the bridge, click **Record** on a controller card, do what you want covered, and click **Stop recording**.
2. Copy the `.s2rec` file from `%LOCALAPPDATA%\Switch2Bridge\recordings` to this folder, with a descriptive name.
3. Save its expected events:

   ```
   dotnet artifacts\bin\Switch2Bridge\release_win-x64\Switch2Bridge.dll --check-recordings tests\recordings --update
   ```

4. Read the new `.events.txt` to check it matches what you did, then commit both files.

`tools\build.ps1` fails if a recording's buttons, sticks, motion or battery change in conversion, or its decoded events stop matching. Recordings hold report bytes and timing, but no Bluetooth address.
