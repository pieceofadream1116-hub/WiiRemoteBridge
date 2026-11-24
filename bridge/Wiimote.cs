using System;
using System.Threading;
using System.Threading.Tasks;

namespace WiiRemoteBridge
{
    // What the remote is doing right now.
    internal sealed class WiiState
    {
        // Core buttons: low byte = report byte 1, high byte = report byte 2
        public ushort Buttons;
        public float Ax, Ay, Az;            // accelerometer in g, the remote's own axes
        public bool HasAccel;
        public byte ReportId;
        public bool HasGyro;
        public bool TwistMode;              // pointer made from wrist twist (no MotionPlus)
        public float PointerX, PointerY;    // our copy of where Dolphin's pointer is (-1..1), twist mode only
        public bool Recentering;
        public bool BarAiming;              // pointer from the sensor bar's light dots right now
        public int BarDots;                 // how many light dots the camera sees
        public float Pitch, Roll, Yaw;       // MotionPlus, degrees per second (+ = the remote's own raw direction)
        public bool HasNunchuk;
        public byte StickX = 128, StickY = 128;
        public bool C, Z;
        public long TimestampUs;

        public const ushort Left = 0x0001, Right = 0x0002, Down = 0x0004, Up = 0x0008, Plus = 0x0010;
        public const ushort Two = 0x0100, One = 0x0200, B = 0x0400, A = 0x0800, Minus = 0x1000, Home = 0x8000;

        public bool Pressed(ushort b) => (Buttons & b) != 0;

        public string ButtonText()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (Pressed(A)) parts.Add("A");
            if (Pressed(B)) parts.Add("B");
            if (Pressed(One)) parts.Add("1");
            if (Pressed(Two)) parts.Add("2");
            if (Pressed(Minus)) parts.Add("-");
            if (Pressed(Plus)) parts.Add("+");
            if (Pressed(Home)) parts.Add("HOME");
            if (Pressed(Up)) parts.Add("UP");
            if (Pressed(Down)) parts.Add("DOWN");
            if (Pressed(Left)) parts.Add("LEFT");
            if (Pressed(Right)) parts.Add("RIGHT");
            if (HasNunchuk && C) parts.Add("C");
            if (HasNunchuk && Z) parts.Add("Z");
            return parts.Count == 0 ? "(none)" : string.Join("  ", parts);
        }
    }

    // Talks the Wii Remote's own language over one HID device.
    internal sealed class Wiimote
    {
        readonly HidDevice dev;
        readonly Action<string> log;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        public event Action<WiiState> StateChanged;
        public bool HasMotionPlus { get; private set; }
        public bool HasNunchuk { get; private set; }
        public DateTime LastReport { get; private set; } = DateTime.Now;
        public long Reports { get; private set; }

        TaskCompletionSource<byte[]> pendingRead, pendingAck, pendingStatus;
        volatile bool setupDone;
        byte mode = 0x31;
        CancellationToken runToken;
        int wrongMode;
        volatile bool gotModeReport;
        DateTime lastModeReport = DateTime.MinValue, lastOtherReport = DateTime.MinValue;
        int resetting;
        readonly int[] idCounts = new int[256];
        DateTime lastIdLog = DateTime.Now;
        string lastIdText = "";

        // Accelerometer calibration (10-bit values)
        int[] accZero = { 512, 512, 512 };
        int[] accOne = { 616, 616, 616 };

        // MotionPlus zero point, found by averaging while the remote sits still
        readonly double[] gyroZero = { 8192, 8192, 8192 };
        readonly double[] calSum = new double[3];
        int calCount;
        volatile bool calibrating;
        const int CalSamples = 150; // about 1.5 seconds

        // Twist aiming (remotes without MotionPlus): the pointer position comes straight from how
        // the wrist is twisted (left/right) and tilted (up/down), sent as an absolute right-stick
        // position that the Dolphin profile uses for pointing. Nothing is added up over time,
        // so it can't drift, and the flips apply in the game.
        const float TwistRange = 40f;     // twisting this far reaches the side edge of the screen
        const float TiltRange = 25f;      // tilting this far reaches the top/bottom edge
        const float HoldStill = 0.3f;     // degrees the wrist must move before the pointer follows
        const float SwingThreshold = 0.35f; // g: quicker movement than this still reaches the game
        const int RecenterHoldMs = 600;   // hold minus this long to make the current aim the centre
        float twistSmooth, tiltSmooth, twistHeld, tiltHeld, twistZero, tiltZero;
        readonly OneEuro twistFilter = new OneEuro(0.8, 0.03), tiltFilter = new OneEuro(0.8, 0.03);
        readonly OneEuro barXFilter = new OneEuro(1.0, 1.5), barYFilter = new OneEuro(1.0, 1.5);
        long lastPointerUs;

        // Sensor bar (camera) aiming
        const float BarGain = 1.25f;      // how far the pointer moves per bit of aiming
        const long BarHoldUs = 250_000;   // keep using the bar this long after the dots disappear
        readonly int[] dotX = new int[4], dotY = new int[4];
        int dotCount;
        long dotsUs;
        double lastMidX = 512, lastMidY = 384, halfX = 60, halfY = 0;
        long lastBarUs = -10_000_000;
        float lastBarX, lastBarY, barZeroX, barZeroY;
        bool lastWasBar;
        float blendFromX, blendFromY;
        long blendStartUs = -10_000_000;
        public static bool AnyBarAiming;
        float gx, gy, gz = 1;             // slow-moving gravity estimate
        bool twistStarted;
        volatile bool recenterRequested;
        long recenterShowUntilUs;
        long minusDownUs = -1;    // when minus went down (twist mode), or -1
        bool minusFired;
        long minusPulseUntilUs;

        // Makes the way the remote is held right now the middle of the screen.
        public void Recenter()
        {
            recenterRequested = true;
            log("Centre set: the pointer's middle is wherever the remote is aimed now.");
        }

        const float SlowUnitsPerDeg = 4352f / 270f;   // same numbers Dolphin uses
        const float FastUnitsPerDeg = 4352f / 1200f;

        public readonly int Player; // 0 = player 1
        public readonly HidDevice Device;

        public Wiimote(HidDevice dev, int player, Action<string> log)
        {
            this.dev = dev;
            Device = dev;
            Player = player;
            this.log = msg => log("Player " + (player + 1) + ": " + msg);
        }

        public void Calibrate()
        {
            Array.Clear(calSum, 0, 3);
            calCount = 0;
            calibrating = true;
            log("Calibrating motion: keep the remote still for a moment...");
        }

        // Runs until the remote stops answering (or cancel). Never throws.
        public async Task RunAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runToken = linked.Token;
            var readTask = ReadLoop(linked.Token);
            try
            {
                await SetupAsync(linked.Token).ConfigureAwait(false);
                setupDone = true;
                await readTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log("Remote stopped: " + ex.Message);
            }
            finally
            {
                linked.Cancel();
                try { await readTask.ConfigureAwait(false); } catch { }
            }
        }

        async Task ReadLoop(CancellationToken ct)
        {
            int silentSeconds = 0;
            while (!ct.IsCancellationRequested)
            {
                byte[] r;
                try { r = await dev.ReadAsync(1000).ConfigureAwait(false); }
                catch (Exception ex) { log("Lost the remote: " + ex.Message); return; }
                if (r == null)
                {
                    if (setupDone && ++silentSeconds >= 3)
                    {
                        log("The remote stopped sending (turned off or out of range?).");
                        return;
                    }
                    if (setupDone) _ = SetMode(mode); // nudge it
                    continue;
                }
                silentSeconds = 0;
                LastReport = DateTime.Now;
                idCounts[r[0]]++;
                if ((DateTime.Now - lastIdLog).TotalSeconds >= 5)
                {
                    LogIds();
                    LogCamera();
                    if (setupDone && resetting == 0 && (DateTime.Now - lastModeReport).TotalSeconds > 3 && (DateTime.Now - lastOtherReport).TotalSeconds < 5)
                    {
                        log("The remote isn't sending tilt data. It was probably reset. Setting it up again.");
                        _ = ResetupAsync();
                    }
                }
                try { Handle(r); } catch (Exception ex) { log("Bad report: " + ex.Message); }
            }
        }

        // Every 5 seconds: which kinds of report came in (only logged when it changes).
        void LogIds()
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 256; i++)
                if (idCounts[i] > 0) parts.Add("0x" + i.ToString("X2") + (i == 0x30 ? " (buttons only)" : i == 0x31 ? " (buttons+tilt)" : i == 0x35 ? " (buttons+tilt+extra)" : ""));
            Array.Clear(idCounts, 0, 256);
            lastIdLog = DateTime.Now;
            string text = string.Join(", ", parts);
            if (text == lastIdText) return;
            lastIdText = text;
            log("Remote is sending: " + (text.Length == 0 ? "nothing" : text) + ". Asked for 0x" + mode.ToString("X2") + ".");
        }

        string lastCamText = "";
        void LogCamera()
        {
            string t = dotCount == 0 ? "camera sees no light dots" : "camera sees " + dotCount + " dot(s):";
            for (int i = 0; i < dotCount; i++) t += " (" + dotX[i] / 100 * 100 + "," + dotY[i] / 100 * 100 + ")";
            if (t == lastCamText) return;
            lastCamText = t;
            log(t);
        }

        // The remote was reset (battery out, re-synced, turned off and on): set it up from scratch.
        async Task ResetupAsync()
        {
            if (Interlocked.Exchange(ref resetting, 1) == 1) return;
            try
            {
                setupDone = false;
                HasMotionPlus = false;
                HasNunchuk = false;
                await SetupAsync(runToken).ConfigureAwait(false);
            }
            catch (Exception ex) { log("Setting the remote up again failed: " + ex.Message); }
            finally
            {
                wrongMode = 0;
                setupDone = true;
                Interlocked.Exchange(ref resetting, 0);
            }
        }

        void Handle(byte[] r)
        {
            byte id = r[0];
            switch (id)
            {
                case 0x20:
                    pendingStatus?.TrySetResult(r);
                    if (setupDone)
                    {
                        // After a status report the remote stops streaming until told again.
                        bool ext = (r[3] & 0x02) != 0;
                        if (!HasMotionPlus && ext != HasNunchuk)
                            log(ext ? "Something was plugged into the remote. Click RECONNECT to use it." : "Nunchuk unplugged.");
                        _ = SetMode(mode);
                    }
                    break;
                case 0x21: pendingRead?.TrySetResult(r); break;
                case 0x22: pendingAck?.TrySetResult(r); break;
                default:
                    if (id >= 0x30 && id <= 0x37)
                    {
                        if (id == mode) { gotModeReport = true; lastModeReport = DateTime.Now; }
                        else lastOtherReport = DateTime.Now;
                        Data(r);
                    }
                    break;
            }
        }

        // ---------- setup ----------

        async Task SetupAsync(CancellationToken ct)
        {
            await FindWorkingMethod().ConfigureAwait(false);
            for (int attempt = 0; attempt < HidDevice.MethodNames.Length; attempt++)
            {
                HasMotionPlus = false;
                HasNunchuk = false;
                await SetupCore(ct).ConfigureAwait(false);

                // Did the remote really switch? Continuous reporting sends ~100 reports a second.
                gotModeReport = false;
                await Task.Delay(1500, ct).ConfigureAwait(false);
                if (gotModeReport)
                {
                    log("Tilt data is coming in, using " + HidDevice.MethodNames[dev.Method] + ".");
                    lastModeReport = DateTime.Now;
                    return;
                }
                int next = (dev.Method + 1) % HidDevice.MethodNames.Length;
                log("No tilt data using " + HidDevice.MethodNames[dev.Method] + ". Trying " + HidDevice.MethodNames[next] + ".");
                dev.Method = next;
            }
            log("The remote ignored every way of sending. Buttons will work, motion won't.");
        }

        // Asks for a status reply with each way of sending until one gets an answer.
        async Task FindWorkingMethod()
        {
            int start = dev.Method < 0 ? 0 : dev.Method;
            for (int i = 0; i < HidDevice.MethodNames.Length; i++)
            {
                dev.Method = (start + i) % HidDevice.MethodNames.Length;
                var status = await RequestStatus().ConfigureAwait(false);
                if (status != null)
                {
                    log("The remote replies to " + HidDevice.MethodNames[dev.Method] + ".");
                    return;
                }
                log("No reply to " + HidDevice.MethodNames[dev.Method] + ".");
            }
            dev.Method = start;
        }

        async Task SetupCore(CancellationToken ct)
        {
            await Send(0x11, (byte)(0x10 << Player)).ConfigureAwait(false); // player's light on, no rumble

            var cal = await ReadMemory(0x00, 0x0016, 10).ConfigureAwait(false);
            if (cal != null)
            {
                int x0 = (cal[0] << 2) | ((cal[3] >> 4) & 3), y0 = (cal[1] << 2) | ((cal[3] >> 2) & 3), z0 = (cal[2] << 2) | (cal[3] & 3);
                int x1 = (cal[4] << 2) | ((cal[7] >> 4) & 3), y1 = (cal[5] << 2) | ((cal[7] >> 2) & 3), z1 = (cal[6] << 2) | (cal[7] & 3);
                if (x1 - x0 > 20 && y1 - y0 > 20 && z1 - z0 > 20)
                {
                    accZero = new[] { x0, y0, z0 };
                    accOne = new[] { x1, y1, z1 };
                    log("Read the remote's motion calibration.");
                }
                else log("Motion calibration looked odd, using standard values.");
            }
            else log("Couldn't read motion calibration, using standard values.");

            // Is a MotionPlus there (built in, or plugged in)?
            var mp = await ReadMemory(0x04, 0xA600FA, 6).ConfigureAwait(false);
            if (mp != null && mp[4] == 0x04 && mp[5] == 0x05)
            {
                await WriteRegister(0xA600F0, 0x55).ConfigureAwait(false);
                await Task.Delay(100, ct).ConfigureAwait(false);
                await WriteRegister(0xA600FE, 0x04).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                HasMotionPlus = true;
                log("MotionPlus found and switched on.");
            }
            else
            {
                var status = await RequestStatus().ConfigureAwait(false);
                bool ext = status != null && (status[3] & 0x02) != 0;
                if (ext)
                {
                    await WriteRegister(0xA400F0, 0x55).ConfigureAwait(false);
                    await WriteRegister(0xA400FB, 0x00).ConfigureAwait(false);
                    var extId = await ReadMemory(0x04, 0xA400FA, 6).ConfigureAwait(false);
                    if (extId != null && extId[4] == 0x00 && extId[5] == 0x00)
                    {
                        HasNunchuk = true;
                        log("Nunchuk found.");
                    }
                    else log("Something is plugged into the remote, but it isn't a Nunchuk. Ignoring it.");
                }
                log("No MotionPlus in this remote: buttons and swings work, but pointing will be jumpy. A MotionPlus remote points smoothly.");
            }

            bool hasExt = HasMotionPlus || HasNunchuk;
            mode = (byte)(hasExt ? 0x35 : 0x31); // buttons + tilt (+ MotionPlus or Nunchuk); camera stays off
            await SetMode(mode).ConfigureAwait(false);
            if (HasMotionPlus) Calibrate();
            log("Asked the remote for report 0x" + mode.ToString("X2") + ".");
        }

        Task<string> Send(params byte[] report) => dev.WriteAsync(report);

        // Continuous reporting in mode m.
        async Task<string> SetMode(byte m)
        {
            string err = await Send(0x12, 0x04, m).ConfigureAwait(false);
            if (err != null) log("Asking for report 0x" + m.ToString("X2") + " failed: " + err);
            return err;
        }

        async Task<byte[]> RequestStatus()
        {
            pendingStatus = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Send(0x15, 0x00).ConfigureAwait(false);
            return await Wait(pendingStatus.Task, 1000).ConfigureAwait(false);
        }

        async Task<byte[]> ReadMemory(byte space, int address, int size)
        {
            pendingRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            string err = await Send(0x17, space, (byte)(address >> 16), (byte)(address >> 8), (byte)address, (byte)(size >> 8), (byte)size).ConfigureAwait(false);
            if (err != null) { log("Read request failed: " + err); return null; }
            var r = await Wait(pendingRead.Task, 1000).ConfigureAwait(false);
            if (r == null || (r[3] & 0x0F) != 0) return null;
            var data = new byte[size];
            Array.Copy(r, 6, data, 0, Math.Min(size, 16));
            return data;
        }

        async Task<bool> WriteRegister(int address, params byte[] data)
        {
            pendingAck = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = new byte[22];
            report[0] = 0x16; report[1] = 0x04;
            report[2] = (byte)(address >> 16); report[3] = (byte)(address >> 8); report[4] = (byte)address;
            report[5] = (byte)data.Length;
            Array.Copy(data, 0, report, 6, Math.Min(16, data.Length));
            string err = await Send(report).ConfigureAwait(false);
            if (err != null) { log("Write failed: " + err); return false; }
            var ack = await Wait(pendingAck.Task, 1000).ConfigureAwait(false);
            return ack != null && ack[4] == 0;
        }

        // Switches on the remote's little camera that sees the sensor bar's light dots.
        async Task<bool> SetupCamera(bool extended)
        {
            await Send(0x13, 0x04).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
            await Send(0x1A, 0x04).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
            bool ok = await WriteRegister(0xB00030, 0x08).ConfigureAwait(false);
            ok &= await WriteRegister(0xB00000, 0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xAA, 0x00, 0x64).ConfigureAwait(false);
            ok &= await WriteRegister(0xB0001A, 0x63, 0x03).ConfigureAwait(false);
            ok &= await WriteRegister(0xB00033, (byte)(extended ? 3 : 1)).ConfigureAwait(false);
            ok &= await WriteRegister(0xB00030, 0x08).ConfigureAwait(false);
            log(ok ? "Camera on: the remote can aim with the sensor bar's lights." : "The camera didn't switch on fully. Twist aiming will still work.");
            return ok;
        }

        static async Task<byte[]> Wait(Task<byte[]> t, int ms)
        {
            return await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false) == t ? t.Result : null;
        }

        // ---------- data ----------

        void Data(byte[] r)
        {
            byte id = r[0];
            var s = new WiiState
            {
                Buttons = (ushort)((r[1] & 0x1F) | ((r[2] & 0x9F) << 8)),
                TimestampUs = clock.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency,
            };

            bool hasAccel = id == 0x31 || id == 0x33 || id == 0x35 || id == 0x37;
            s.HasAccel = hasAccel;
            s.ReportId = id;
            if (hasAccel)
            {
                int ax = (r[3] << 2) | ((r[1] >> 5) & 3);
                int ay = (r[4] << 2) | (((r[2] >> 5) & 1) << 1);
                int az = (r[5] << 2) | (((r[2] >> 6) & 1) << 1);
                s.Ax = (ax - accZero[0]) / (float)(accOne[0] - accZero[0]);
                s.Ay = (ay - accZero[1]) / (float)(accOne[1] - accZero[1]);
                s.Az = (az - accZero[2]) / (float)(accOne[2] - accZero[2]);
            }
            else s.Az = 1;

            if (id == 0x33 && r.Length >= 18) ParseCamera(r, 6, true, s.TimestampUs);
            else if (id == 0x37 && r.Length >= 16) ParseCamera(r, 6, false, s.TimestampUs);

            // Plain go-between, like WiimoteHook: buttons, the real tilt and MotionPlus go to Dolphin
            // untouched, and Dolphin does the pointing itself from MotionPlus.

            int extAt = id == 0x32 || id == 0x34 ? 3 : id == 0x35 ? 6 : id == 0x36 ? 13 : id == 0x37 ? 16 : -1;
            if (extAt > 0 && r.Length >= extAt + 6)
            {
                if (HasMotionPlus) MotionPlusData(r, extAt, s);
                else if (HasNunchuk)
                {
                    s.HasNunchuk = true;
                    s.StickX = r[extAt];
                    s.StickY = r[extAt + 1];
                    s.Z = (r[extAt + 5] & 0x01) == 0;
                    s.C = (r[extAt + 5] & 0x02) == 0;
                }
            }

            Reports++;
            StateChanged?.Invoke(s);
        }

        // In twist mode, holding minus re-centres the pointer instead of pressing minus.
        // A quick tap still reaches the game as a short minus press.
        void MinusHold(WiiState s)
        {
            bool down = s.Pressed(WiiState.Minus);
            s.Buttons = (ushort)(s.Buttons & ~WiiState.Minus);
            if (down)
            {
                if (minusDownUs < 0) { minusDownUs = s.TimestampUs; minusFired = false; }
                else if (!minusFired && s.TimestampUs - minusDownUs >= RecenterHoldMs * 1000L)
                {
                    minusFired = true;
                    Recenter();
                }
            }
            else if (minusDownUs >= 0)
            {
                if (!minusFired) minusPulseUntilUs = s.TimestampUs + 120_000;
                minusDownUs = -1;
            }
            if (s.TimestampUs < minusPulseUntilUs) s.Buttons |= WiiState.Minus;
        }

        void ParseCamera(byte[] r, int at, bool extended, long nowUs)
        {
            int n = 0;
            if (extended)
            {
                for (int i = 0; i < 4; i++)
                {
                    int o = at + i * 3;
                    byte b0 = r[o], b1 = r[o + 1], b2 = r[o + 2];
                    int x = b0 | (((b2 >> 4) & 3) << 8), y = b1 | (((b2 >> 6) & 3) << 8);
                    if (x >= 1023 || y >= 1023 || (x == 0 && y == 0)) continue; // empty, or camera not really on
                    dotX[n] = x; dotY[n] = y; n++;
                }
            }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    int o = at + i * 5;
                    byte b0 = r[o], b1 = r[o + 1], b2 = r[o + 2], b3 = r[o + 3], b4 = r[o + 4];
                    int x1 = b0 | (((b2 >> 4) & 3) << 8), y1 = b1 | (((b2 >> 6) & 3) << 8);
                    int x2 = b3 | ((b2 & 3) << 8), y2 = b4 | (((b2 >> 2) & 3) << 8);
                    if (x1 < 1023 && y1 < 1023 && (x1 | y1) != 0) { dotX[n] = x1; dotY[n] = y1; n++; }
                    if (x2 < 1023 && y2 < 1023 && (x2 | y2) != 0) { dotX[n] = x2; dotY[n] = y2; n++; }
                }
            }
            dotCount = n;
            dotsUs = nowUs;
        }

        // Where the sensor bar says the remote is aiming (-1..1, up and right positive), if it can see it.
        bool BarPointer(long nowUs, out float px, out float py, out float rawX, out float rawY)
        {
            px = py = rawX = rawY = 0;
            double midX, midY;
            if (dotCount >= 2 && nowUs - dotsUs < 100_000)
            {
                // The two dots furthest apart are the two ends of the bar.
                int a = 0, b = 1; double best = -1;
                for (int i = 0; i < dotCount; i++)
                    for (int j = i + 1; j < dotCount; j++)
                    {
                        double d = Math.Pow(dotX[i] - dotX[j], 2) + Math.Pow(dotY[i] - dotY[j], 2);
                        if (d > best) { best = d; a = i; b = j; }
                    }
                // A real bar's two ends are a sensible distance apart; anything else isn't the bar.
                if (best < 15 * 15 || best > 900 * 900) return HoldOrNot(nowUs, out px, out py);
                if (dotX[a] > dotX[b]) (a, b) = (b, a);
                midX = (dotX[a] + dotX[b]) / 2.0;
                midY = (dotY[a] + dotY[b]) / 2.0;
                halfX = (dotX[b] - dotX[a]) / 2.0;
                halfY = (dotY[b] - dotY[a]) / 2.0;
            }
            else if (dotCount == 1 && nowUs - dotsUs < 100_000 && nowUs - lastBarUs < 1_000_000)
            {
                // One dot: it's one end of the bar. Pick the end that keeps the middle closest to before.
                double m1x = dotX[0] - halfX, m1y = dotY[0] - halfY, m2x = dotX[0] + halfX, m2y = dotY[0] + halfY;
                bool first = Math.Pow(m1x - lastMidX, 2) + Math.Pow(m1y - lastMidY, 2) <= Math.Pow(m2x - lastMidX, 2) + Math.Pow(m2y - lastMidY, 2);
                midX = first ? m1x : m2x;
                midY = first ? m1y : m2y;
            }
            else return HoldOrNot(nowUs, out px, out py);

            lastMidX = midX; lastMidY = midY;

            // Undo the wrist's twist: turn the picture so the bar is level again.
            double angle = Math.Atan2(halfY, halfX);
            double cx = midX - 512, cy = midY - 384;
            double rx = cx * Math.Cos(-angle) - cy * Math.Sin(-angle);
            double ry = cx * Math.Sin(-angle) + cy * Math.Cos(-angle);

            // The camera's picture is mirrored: aiming right or up moves the dots towards higher numbers.
            rawX = (float)(rx / 512 * BarGain);
            rawY = (float)(ry / 384 * BarGain);
            px = Math.Clamp(rawX - barZeroX, -1, 1) * (Settings.IrFlipLeftRight ? -1 : 1);
            py = Math.Clamp(rawY - barZeroY, -1, 1) * (Settings.IrFlipUpDown ? -1 : 1);
            lastBarX = px; lastBarY = py; lastBarUs = nowUs;
            return true;
        }

        // Brief blink: hold where it was for a moment; after that, the bar isn't seen.
        bool HoldOrNot(long nowUs, out float px, out float py)
        {
            px = lastBarX; py = lastBarY;
            return nowUs - lastBarUs < BarHoldUs;
        }

        void TwistPointer(WiiState s)
        {
            float twist = (float)(Math.Atan2(s.Ax, s.Az) * 180 / Math.PI);                                   // roll
            float tilt = (float)(Math.Atan2(s.Ay, Math.Sqrt(s.Ax * s.Ax + s.Az * s.Az)) * 180 / Math.PI);   // pitch
            if (!twistStarted)
            {
                twistHeld = twist; tiltHeld = tilt;
                gx = s.Ax; gy = s.Ay; gz = s.Az;
                lastPointerUs = s.TimestampUs;
                twistStarted = true;
            }
            double dt = Math.Clamp((s.TimestampUs - lastPointerUs) / 1_000_000.0, 0.002, 0.05);
            lastPointerUs = s.TimestampUs;

            // Twist aiming: steady when still, quick when moving, then ignore the tiniest wobble.
            twistSmooth = (float)twistFilter.Filter(twist, dt);
            tiltSmooth = (float)tiltFilter.Filter(tilt, dt);
            if (Math.Abs(twistSmooth - twistHeld) > HoldStill) twistHeld += Math.Sign(twistSmooth - twistHeld) * (Math.Abs(twistSmooth - twistHeld) - HoldStill);
            if (Math.Abs(tiltSmooth - tiltHeld) > HoldStill) tiltHeld += Math.Sign(tiltSmooth - tiltHeld) * (Math.Abs(tiltSmooth - tiltHeld) - HoldStill);

            // Sensor bar aiming, when the camera can see the bar.
            bool seen = BarPointer(s.TimestampUs, out float bx, out float by, out float rawBx, out float rawBy);
            // Only switch to the bar once it has been seen steadily for a moment, so a flicker
            // can't keep restarting the glide (which froze the pointer).
            if (!seen) barSeenSinceUs = -1;
            else if (barSeenSinceUs < 0) barSeenSinceUs = s.TimestampUs;
            bool bar = seen && s.TimestampUs - barSeenSinceUs >= 150_000;

            if (recenterRequested)
            {
                recenterRequested = false;
                twistZero = twistHeld;
                tiltZero = tiltHeld;
                if (bar && dotCount > 0) { barZeroX = rawBx; barZeroY = rawBy; bar = BarPointer(s.TimestampUs, out bx, out by, out _, out _); }
                recenterShowUntilUs = s.TimestampUs + 700_000;
            }

            float x, y;
            if (bar)
            {
                x = (float)barXFilter.Filter(bx, dt);
                y = (float)barYFilter.Filter(by, dt);
            }
            else
            {
                barXFilter.Reset(); barYFilter.Reset();
                if (Settings.TwistAiming)
                {
                    x = Math.Clamp((twistHeld - twistZero) / TwistRange, -1, 1) * (Settings.FlipLeftRight ? -1 : 1);
                    y = Math.Clamp((tiltHeld - tiltZero) / TiltRange, -1, 1) * (Settings.FlipUpDown ? -1 : 1);
                }
                else x = y = 0;
            }

            // Glide (0.15 s) instead of jumping when switching between bar and twist aiming.
            if (bar != lastWasBar) { blendFromX = lastOutX; blendFromY = lastOutY; blendStartUs = s.TimestampUs; lastWasBar = bar; }
            float k = Math.Clamp((s.TimestampUs - blendStartUs) / 150_000f, 0, 1);
            x = blendFromX + (x - blendFromX) * k;
            y = blendFromY + (y - blendFromY) * k;
            lastOutX = x; lastOutY = y;

            // The tilt sensor (and MotionPlus) go to the game exactly as the remote reports them,
            // so swings, power bars and steering all work. Dolphin's own motion pointing is off in
            // the profile; the pointer comes only from the stick.
            s.TwistMode = true;
            s.BarAiming = bar;
            s.BarDots = s.TimestampUs - dotsUs < 100_000 ? dotCount : 0;
            AnyBarAiming = bar;
            s.Recentering = s.TimestampUs < recenterShowUntilUs;
            s.PointerX = x;
            s.PointerY = y;
        }
        float lastOutX, lastOutY;
        long barSeenSinceUs = -1;

        void MotionPlusData(byte[] r, int at, WiiState s)
        {
            byte e0 = r[at], e1 = r[at + 1], e2 = r[at + 2], e3 = r[at + 3], e4 = r[at + 4], e5 = r[at + 5];
            if ((e5 & 0x02) == 0) return; // not MotionPlus data
            int yaw = e0 | ((e3 >> 2) << 8);
            int roll = e1 | ((e4 >> 2) << 8);
            int pitch = e2 | ((e5 >> 2) << 8);
            bool yawSlow = (e3 & 0x02) != 0, pitchSlow = (e3 & 0x01) != 0, rollSlow = (e4 & 0x02) != 0;

            if (calibrating)
            {
                if (yawSlow && pitchSlow && rollSlow)
                {
                    calSum[0] += pitch; calSum[1] += roll; calSum[2] += yaw;
                    if (++calCount >= CalSamples)
                    {
                        for (int i = 0; i < 3; i++) gyroZero[i] = calSum[i] / calCount;
                        calibrating = false;
                        log("Motion calibrated.");
                    }
                }
                s.HasGyro = true; // report zero while calibrating
                return;
            }

            s.HasGyro = true;
            s.Pitch = (float)((pitch - gyroZero[0]) / (pitchSlow ? SlowUnitsPerDeg : FastUnitsPerDeg));
            s.Roll = (float)((roll - gyroZero[1]) / (rollSlow ? SlowUnitsPerDeg : FastUnitsPerDeg));
            s.Yaw = (float)((yaw - gyroZero[2]) / (yawSlow ? SlowUnitsPerDeg : FastUnitsPerDeg));
        }

        // ---------- finding which device has a live remote ----------

        // Asks one device for its status using each way of sending in turn.
        // Only a real status reply (0x20) counts: button reports just mean the remote is on,
        // not that our messages reach it. Returns the way that worked, or -1.
        // If the remote sends buttons but never replies, returns 1 and the setup tries every way.
        public static async Task<int> ProbeAsync(HidDevice d, System.Text.StringBuilder why)
        {
            bool sawData = false;
            for (int m = 0; m < HidDevice.MethodNames.Length; m++)
            {
                string err = await d.WriteAsync(new byte[] { 0x15, 0x00 }, m).ConfigureAwait(false);
                if (err != null)
                {
                    why.Append("   ").Append(HidDevice.MethodNames[m]).Append(": ").AppendLine(err);
                    continue;
                }
                var until = DateTime.Now.AddMilliseconds(1000);
                while (DateTime.Now < until)
                {
                    byte[] r;
                    try { r = await d.ReadAsync(400).ConfigureAwait(false); }
                    catch (Exception ex) { why.Append("   reading: ").AppendLine(ex.Message); return -1; }
                    if (r == null) continue;
                    if (r[0] == 0x20) return m;
                    if (r[0] >= 0x30 && r[0] <= 0x3F) sawData = true;
                }
                why.Append("   ").Append(HidDevice.MethodNames[m]).AppendLine(": sent, but no status reply");
            }
            if (sawData)
            {
                why.AppendLine("   The remote is on (button data arrives) but never replied. Will try every way during setup.");
                return 1;
            }
            return -1;
        }
    }
}
