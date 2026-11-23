# Wii Remote Pairer

A small, big-button Windows app for finding and pairing Wii Remotes, including
third-party copies, so they can be used with WiimoteHook and Dolphin.

Mouse only. No typing.

## Easiest way: AUTO-PAIR

1. Close WiimoteHook.
2. Open **WiiRemotePairer.exe** and click **Yes** when Windows asks for permission.
3. Click the orange **AUTO-PAIR** button.
4. Press the red **sync** button in the remote's battery cover.
5. The app pairs it by itself as soon as it sees it. If the remote goes to sleep,
   big red text says **PRESS THE RED BUTTON AGAIN**, and it keeps trying for 30 seconds.

## Doing it by hand

1. Close WiimoteHook.
2. Open **WiiRemotePairer.exe** and click **Yes** when Windows asks for permission.
3. Click **Start scanning**.
4. Open the remote's battery cover and press the small red **sync** button. The lights blink.
5. Every Bluetooth device nearby is listed, including ones with no name.
   Anything that looks like a Wii Remote is shown in **green**.
6. Click the remote, then click **Pair this remote** while its lights are still blinking.
7. Open WiimoteHook.

## How pairing works

Wii Remotes don't use a normal PIN. The app tries three ways in turn, the same
way the WiiPair tools do:

1. PIN = this PC's Bluetooth address (red sync button)
2. PIN = the remote's own address (buttons 1 + 2)
3. No PIN (some third-party remotes)

Then it switches on the remote's controller (HID) service.

**Forget this device** removes an old, stale pairing so you can start fresh.

## Building

GitHub Actions builds it on every push (`.github/workflows/build.yml`) and puts
`WiiRemotePairer.exe` on the Releases page. To build yourself with the .NET 8 SDK:

```
dotnet publish src/WiiRemotePairer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

## License

MIT

# Wii Remote Bridge

`WiiRemoteBridge.exe` is an open-source stand-in for WiimoteHook. It reads a Wii Remote
through a **Mayflash Dolphin Bar on mode 4** (or a remote paired over Bluetooth) and serves it
as a DSU / Cemuhook motion source on UDP port **26760**, so Dolphin on another device (like an
Xbox in Dev Mode) can use it under *Alternate Input Sources > DSU*.

- Up to 4 remotes at once: each remote synced to the bar becomes its own player
  (first = DSU pad 0 = player 1, second = pad 1 = player 2, ...), with its player light lit.
- Buttons, accelerometer, MotionPlus (auto-calibrated) and Nunchuk.
- Remotes without MotionPlus aim by **wrist twist** (left/right) and **tilt** (up/down). The aim is
  sent as an absolute position on the DSU right stick, which the Dolphin "WiimoteHook" profile maps
  to the IR (Point) group, so it can't drift. The tilt sensor Dolphin gets keeps the real sideways
  twist and quick swings, without the up/down tilt (the stick does that). Adaptive smoothing plus a
  small hold-still zone keep it steady. Hold **minus** ~0.6 s (or click "Set centre") to make the
  current aim the middle; a quick tap still presses minus. "Flip left/right" / "Flip up/down" and
  "Twist aiming" on/off are remembered in `WiiRemoteBridge-settings.txt`. A small test screen shows
  each player's pointer.
- Each player's data only goes to the Dolphin connection that asked for that player (Dolphin
  opens one per controller), so two remotes never get mixed together.
- Laid out like WiimoteHook's DS4 output: A = Cross, B = R1, 1 = Square, 2 = Circle,
  - = Share, + = Options, Home = Triangle, Nunchuk C/Z = L3/R3, Nunchuk stick = left stick.
- Tries four ways of sending to each Dolphin Bar slot and shows which one works, with the
  Windows error for the ones that don't. Empty slots are skipped, never a crash.
- Writes `WiiRemoteBridge-log.txt` next to the app.

Close WiimoteHook first: both use port 26760.
