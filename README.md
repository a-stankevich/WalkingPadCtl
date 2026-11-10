# WalkingPadCtl

Command-line speed control for a Kingsmith WalkingPad over BLE on Windows.
Protocol taken from [QWalkingPad](https://github.com/DorianRudolph/QWalkingPad).

```powershell
./Set-WalkingPadSpeed.ps1 -Speed 35   # 3.5 km/h (starts the belt if needed)
./Set-WalkingPadSpeed.ps1 -Speed 0    # stop
./Set-WalkingPadSpeed.ps1 -Speed 30 -Verbose   # show BLE traffic
./Set-WalkingPadSpeed.ps1               # read mode: print state + settings, change nothing
(./Set-WalkingPadSpeed.ps1 -Json).speed # read mode as a PowerShell object
```

`-Speed` is in 0.1 km/h units (QWalkingPad's convention); valid values are 0 or 5..max speed.
The script builds `bin\walkingpad.exe` on first run (needs the .NET 10 SDK); the exe can also be called directly.

Device lookup order: `-Address`, cached address (`%LOCALAPPDATA%\WalkingPadCtl\address.txt`),
QWalkingPad's last device (registry), then a 15 s BLE scan (`-Scan` forces it).
The pad accepts only one BLE connection, so close the phone app / QWalkingPad first.

Protocol notes learned while testing (A1 Pro):
- Speed 0 (`F7 A2 01 00`) does not stop the belt; it only drops to the 0.8 km/h minimum.
  Stopping uses the start command (`F7 A2 04 01`), which toggles the belt off while running.
- After start the pad counts down (state 9 → 7) before state 1 (running); speed is set after that.
- The first query after connecting is sometimes ignored, so it is retried.
