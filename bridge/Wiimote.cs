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

        const float SlowUnitsPerDeg = 4352f / 270f;   // same numbers Dolphin uses
        const float FastUnitsPerDeg = 4352f / 1200f;

        public Wiimote(HidDevice dev, Action<string> log) { this.dev = dev; this.log = log; }

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
                if ((DateTime.Now - lastIdLog).TotalSeconds >= 5) LogIds();
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
                        if (setupDone && id != mode)
                        {
                            wrongMode++;
                            if (wrongMode == 5) _ = SetMode(mode); // first, just ask again
                            if (wrongMode == 40)
                            {
                                log("The remote is sending 0x" + id.ToString("X2") + " instead of 0x" + mode.ToString("X2") + ". It was probably reset. Setting it up again.");
                                _ = ResetupAsync();
                            }
                        }
                        else if (id == mode) wrongMode = 0;
                        Data(r);
                    }
                    break;
            }
        }

        // ---------- setup ----------

        async Task SetupAsync(CancellationToken ct)
        {
            await Send(0x11, 0x10).ConfigureAwait(false); // first light on, no rumble

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
                log("No MotionPlus: motion will use tilt only.");
            }

            mode = (byte)(HasMotionPlus || HasNunchuk ? 0x35 : 0x31);
            await SetMode(mode).ConfigureAwait(false);
            if (HasMotionPlus) Calibrate();
            log("Remote is set up and streaming.");
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

        async Task<bool> WriteRegister(int address, byte value)
        {
            pendingAck = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = new byte[22];
            report[0] = 0x16; report[1] = 0x04;
            report[2] = (byte)(address >> 16); report[3] = (byte)(address >> 8); report[4] = (byte)address;
            report[5] = 1; report[6] = value;
            string err = await Send(report).ConfigureAwait(false);
            if (err != null) { log("Write failed: " + err); return false; }
            var ack = await Wait(pendingAck.Task, 1000).ConfigureAwait(false);
            return ack != null && ack[4] == 0;
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
        // Returns the way that worked, or -1 (with the reasons in 'why').
        public static async Task<int> ProbeAsync(HidDevice d, System.Text.StringBuilder why)
        {
            for (int m = 0; m < HidDevice.MethodNames.Length; m++)
            {
                string err = await d.WriteAsync(new byte[] { 0x15, 0x00 }, m).ConfigureAwait(false);
                if (err != null)
                {
                    why.Append("   ").Append(HidDevice.MethodNames[m]).Append(": ").AppendLine(err);
                    continue;
                }
                // Wait up to a second for an answer (a status report, or any data if it's already streaming).
                var until = DateTime.Now.AddMilliseconds(1000);
                while (DateTime.Now < until)
                {
                    byte[] r;
                    try { r = await d.ReadAsync(400).ConfigureAwait(false); }
                    catch (Exception ex) { why.Append("   reading: ").AppendLine(ex.Message); return -1; }
                    if (r == null) continue;
                    if (r[0] == 0x20 || (r[0] >= 0x30 && r[0] <= 0x37) || r[0] == 0x22) return m;
                }
                why.Append("   ").Append(HidDevice.MethodNames[m]).AppendLine(": sent, but no answer");
            }
            return -1;
        }
    }
}
