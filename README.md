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
