using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WiiRemoteBridge
{
    internal sealed class BridgeForm : Form
    {
        const int MaxPlayers = 4;

        readonly Label status = new Label();
        readonly Label barLine = new Label();
        readonly Label xboxLine = new Label();
        readonly Label[] playerLines = new Label[MaxPlayers];
        readonly Button startBtn = new Button();
        readonly Button calibrateBtn = new Button();
        readonly Button reconnectBtn = new Button();
        readonly Button flipXBtn = new Button();
        readonly Button flipYBtn = new Button();
        readonly Button twistBtn = new Button();
        readonly Button recenterBtn = new Button();
        readonly PointerPreview preview = new PointerPreview();
        readonly TextBox logBox = new TextBox();

        static readonly Font Big = new Font("Segoe UI", 16f);
        static readonly Font BigBold = new Font("Segoe UI", 16f, FontStyle.Bold);
        static readonly Font Huge = new Font("Segoe UI", 28f, FontStyle.Bold);
        static readonly Color Ink = Color.FromArgb(30, 30, 30);
        static readonly Color Dim = Color.FromArgb(120, 120, 120);
        static readonly Color Good = Color.FromArgb(20, 130, 60);
        static readonly Color Warn = Color.FromArgb(200, 90, 0);
        static readonly Color Bad = Color.FromArgb(190, 30, 30);

        // One per player (index 0 = player 1 = Dolphin's first DSU pad).
        sealed class Seat
        {
            public Wiimote Remote;
            public volatile WiiState Latest;
            public CancellationTokenSource Cts;
        }
        readonly Seat[] seats = new Seat[MaxPlayers];
        readonly object seatGate = new object();

        readonly string logPath;
        readonly object logGate = new object();
        DsuServer server;
        CancellationTokenSource runCts;
        volatile bool closing;
        string lastProbeReport = "";

        public BridgeForm()
        {
            Text = "Wii Remote Bridge";
            Font = Big;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(900, 650);
            BackColor = Color.FromArgb(250, 250, 247);
            for (int i = 0; i < MaxPlayers; i++) seats[i] = new Seat();

            logPath = Path.Combine(AppContext.BaseDirectory, "WiiRemoteBridge-log.txt");
            try { File.WriteAllText(logPath, "Wii Remote Bridge log, " + DateTime.Now + Environment.NewLine); }
            catch { logPath = Path.Combine(Path.GetTempPath(), "WiiRemoteBridge-log.txt"); }

            int rows = 6 + MaxPlayers + 1;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Padding = new Padding(16) };
            for (int i = 0; i < rows - 1; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            status.AutoSize = true;
            status.Font = Huge;
            status.Margin = new Padding(0, 0, 0, 10);
            status.MaximumSize = new Size(1800, 0);
            layout.Controls.Add(status);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            Style(startBtn, "START", Good, 260);
            Style(calibrateBtn, "Calibrate motion", Color.FromArgb(32, 96, 200), 300);
            Style(reconnectBtn, "Reconnect remotes", Warn, 320);
            startBtn.Click += (s, e) => ToggleStart();
            calibrateBtn.Click += (s, e) => { foreach (var seat in seats) seat.Remote?.Calibrate(); };
            reconnectBtn.Click += (s, e) =>
            {
                Log("Reconnecting all remotes...");
                foreach (var seat in seats) seat.Cts?.Cancel();
            };
            Settings.Load();
            Style(flipXBtn, "", Color.FromArgb(90, 70, 150), 300);
            Style(flipYBtn, "", Color.FromArgb(90, 70, 150), 300);
            flipXBtn.Click += (s, e) => { Settings.FlipLeftRight = !Settings.FlipLeftRight; Settings.Save(); FlipText(); Log("Left/right " + (Settings.FlipLeftRight ? "flipped." : "back to normal.")); };
            flipYBtn.Click += (s, e) => { Settings.FlipUpDown = !Settings.FlipUpDown; Settings.Save(); FlipText(); Log("Up/down " + (Settings.FlipUpDown ? "flipped." : "back to normal.")); };
            Style(twistBtn, "", Color.FromArgb(90, 70, 150), 300);
            Style(recenterBtn, "Re-centre pointers", Color.FromArgb(32, 96, 200), 320);
            twistBtn.Click += (s, e) => { Settings.TwistAiming = !Settings.TwistAiming; Settings.Save(); FlipText(); Log("Twist aiming " + (Settings.TwistAiming ? "on." : "off: tilt only, like before.")); };
            recenterBtn.Click += (s, e) => { foreach (var seat in seats) seat.Remote?.Recenter(); };
            FlipText();
            buttons.Controls.Add(startBtn);
            buttons.Controls.Add(calibrateBtn);
            buttons.Controls.Add(reconnectBtn);
            buttons.Controls.Add(flipXBtn);
            buttons.Controls.Add(flipYBtn);
            buttons.Controls.Add(twistBtn);
            buttons.Controls.Add(recenterBtn);
            layout.Controls.Add(buttons);

            foreach (var l in new[] { barLine, xboxLine })
            {
                l.AutoSize = true;
                l.Font = BigBold;
                l.Margin = new Padding(0, 4, 0, 4);
                l.MaximumSize = new Size(1800, 0);
                layout.Controls.Add(l);
            }
            for (int i = 0; i < MaxPlayers; i++)
            {
                playerLines[i] = new Label { AutoSize = true, Font = BigBold, Margin = new Padding(0, 6, 0, 6), MaximumSize = new Size(1800, 0) };
                layout.Controls.Add(playerLines[i]);
            }

            var pcIp = new Label { AutoSize = true, Font = Big, Margin = new Padding(0, 6, 0, 8), Text = "This PC's address for Dolphin:  " + LocalAddresses() + "   port " + DsuServer.DefaultPort };
            layout.Controls.Add(pcIp);

            preview.Seats = () => seats.Select(x => (x.Remote != null, x.Latest)).ToArray();
            layout.Controls.Add(preview);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Segoe UI", 12.5f);
            logBox.BackColor = Color.White;
            layout.Controls.Add(logBox);

            SetStatus("Click START.", Ink);
            barLine.Text = "Dolphin Bar:  -";
            xboxLine.Text = "Xbox:  -";
            calibrateBtn.Enabled = reconnectBtn.Enabled = false;
            RefreshLive();

            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (s, e) => RefreshLive();
            timer.Start();

            Shown += (s, e) => ToggleStart(); // start straight away
            FormClosing += (s, e) => { closing = true; runCts?.Cancel(); server?.Dispose(); };
        }

        static void Style(Button b, string text, Color c, int width)
        {
            b.Text = text;
            b.Size = new Size(width, 84);
            b.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
            b.BackColor = c;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Margin = new Padding(0, 0, 14, 8);
            b.Cursor = Cursors.Hand;
        }

        void FlipText()
        {
            flipXBtn.Text = Settings.FlipLeftRight ? "Left/right: FLIPPED" : "Flip left/right";
            flipYBtn.Text = Settings.FlipUpDown ? "Up/down: FLIPPED" : "Flip up/down";
            twistBtn.Text = Settings.TwistAiming ? "Twist aiming: ON" : "Twist aiming: OFF";
        }

        static string LocalAddresses()
        {
            try
            {
                var list = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254"))
                    .Select(a => a.Address.ToString())
                    .Distinct().ToList();
                return list.Count == 0 ? "(no network)" : string.Join("  or  ", list);
            }
            catch { return "(unknown)"; }
        }

        // ---------- logging ----------

        void Log(string s)
        {
            string line = DateTime.Now.ToString("h:mm:ss tt") + "   " + s;
            lock (logGate) { try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { } }
            if (closing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => AddLog(line))); } catch { } return; }
            AddLog(line);
        }

        void AddLog(string line)
        {
            if (logBox.TextLength > 200_000) logBox.Clear();
            logBox.AppendText(line + Environment.NewLine);
        }

        void SetStatus(string s, Color c)
        {
            if (closing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SetStatus(s, c))); } catch { } return; }
            status.Text = s;
            status.ForeColor = c;
        }

        void SetLine(Label l, string s, Color c)
        {
            if (closing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SetLine(l, s, c))); } catch { } return; }
            l.Text = s;
            l.ForeColor = c;
        }

        // ---------- start / stop ----------

        void ToggleStart()
        {
            if (runCts != null)
            {
                runCts.Cancel();
                runCts = null;
                server?.Dispose();
                server = null;
                startBtn.Text = "START";
                startBtn.BackColor = Good;
                SetStatus("Stopped. Click START.", Ink);
                calibrateBtn.Enabled = reconnectBtn.Enabled = false;
                return;
            }

            server = new DsuServer(Log) { RemoteConnected = pad => pad < MaxPlayers && seats[pad].Remote != null && seats[pad].Latest != null };
            if (!server.Start(DsuServer.DefaultPort, out string err))
            {
                SetStatus("Can't start: " + err, Bad);
                Log(err);
                server = null;
                return;
            }
            Log("Listening for Dolphin on port " + DsuServer.DefaultPort + ". This PC: " + LocalAddresses());
            Log("If Windows asks about the firewall, click Allow.");
            runCts = new CancellationTokenSource();
            startBtn.Text = "STOP";
            startBtn.BackColor = Bad;
            reconnectBtn.Enabled = calibrateBtn.Enabled = true;
            var token = runCts.Token;
            _ = Task.Run(() => ScanLoop(token));
        }

        // Every few seconds: look at every Dolphin Bar slot that doesn't already have a player,
        // and give any remote that answers the lowest free player number.
        async Task ScanLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ScanOnce(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log("Problem: " + ex.Message); }
                try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        async Task ScanOnce(CancellationToken ct)
        {
            var devices = HidDevice.FindWiimotes();
            if (devices.Count == 0)
            {
                SetLine(barLine, "Dolphin Bar:  not found. Is it plugged in and on mode 4?", Bad);
                LogOnce("No Nintendo devices found (looking for 057E:0306 / 057E:0330).");
                return;
            }
            SetLine(barLine, "Dolphin Bar:  found (" + devices.Count + (devices.Count == 1 ? " slot" : " slots") + ")", Good);

            HashSet<string> inUse;
            lock (seatGate)
                inUse = new HashSet<string>(seats.Where(x => x.Remote != null).Select(x => x.Remote.Device.Path), StringComparer.OrdinalIgnoreCase);
            if (inUse.Count >= MaxPlayers) return;

            var report = new StringBuilder();
            int checkedCount = 0;
            foreach (var d in devices)
            {
                if (ct.IsCancellationRequested) return;
                if (inUse.Contains(d.Path)) continue;
                checkedCount++;
                report.AppendLine(" Slot " + d.Slot + " (product " + d.Pid.ToString("X4") + ", in " + d.InLen + " / out " + d.OutLen + " bytes):");
                if (!d.Open(out string openErr))
                {
                    report.AppendLine("   couldn't open: " + openErr);
                    continue;
                }
                int method = await Wiimote.ProbeAsync(d, report).ConfigureAwait(false);
                if (method < 0) { d.Dispose(); continue; }

                d.Method = method;
                report.AppendLine("   ANSWERED using " + HidDevice.MethodNames[method]);
                int player = TakeSeat(d, ct);
                if (player < 0) { d.Dispose(); break; }
                Log("Remote found in slot " + d.Slot + ": it's player " + (player + 1) + ".");
            }
            if (checkedCount > 0) LogOnce("Checked " + checkedCount + " free slot(s):" + Environment.NewLine + report.ToString().TrimEnd());
        }

        // Gives the remote the lowest free player number and starts it. Returns the player, or -1.
        int TakeSeat(HidDevice d, CancellationToken ct)
        {
            Seat seat;
            int player;
            lock (seatGate)
            {
                player = Array.FindIndex(seats, x => x.Remote == null);
                if (player < 0) return -1;
                seat = seats[player];
                seat.Remote = new Wiimote(d, player, Log);
                seat.Latest = null;
                seat.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }
            var w = seat.Remote;
            int pad = player;
            w.StateChanged += s => { seat.Latest = s; server?.SendState(pad, s); };
            var token = seat.Cts.Token;
            _ = Task.Run(async () =>
            {
                try { await w.RunAsync(token).ConfigureAwait(false); }
                catch (Exception ex) { Log("Player " + (pad + 1) + ": " + ex.Message); }
                lock (seatGate)
                {
                    seat.Remote = null;
                    seat.Latest = null;
                }
                d.Dispose();
                if (!closing && !ct.IsCancellationRequested)
                    Log("Player " + (pad + 1) + " disconnected. Press A on that remote to bring it back.");
            });
            return player;
        }

        // Only write a probe report to the log when it changes, so the log stays readable.
        void LogOnce(string text)
        {
            if (text == lastProbeReport) return;
            lastProbeReport = text;
            Log(text);
        }

        // ---------- live display ----------

        void RefreshLive()
        {
            preview.Invalidate();
            int connected = 0;
            for (int i = 0; i < MaxPlayers; i++)
            {
                var seat = seats[i];
                var w = seat.Remote;
                var s = seat.Latest;
                var l = playerLines[i];
                string name = "Player " + (i + 1) + ":  ";
                if (w == null)
                {
                    l.Text = name + (i == 0 ? "no remote yet. Press A on it, or the bar's SYNC then the remote's red button." : "free. Sync another remote to the bar to add a player.");
                    l.ForeColor = i == 0 ? Warn : Dim;
                    continue;
                }
                if (s == null)
                {
                    l.Text = name + "setting up the remote in slot " + w.Device.Slot + "...";
                    l.ForeColor = Warn;
                    continue;
                }
                connected++;
                string extras = (w.HasMotionPlus ? " + MotionPlus" : "") + (w.HasNunchuk ? " + Nunchuk" : "");
                string motion = s.HasAccel
                    ? string.Format("tilt {0:+0.0;-0.0} {1:+0.0;-0.0} {2:+0.0;-0.0}", s.Ax, s.Ay, s.Az)
                    : "no tilt data yet (0x" + s.ReportId.ToString("X2") + "), fixing...";
                if (s.TwistMode) motion += s.Recentering ? "   pointer: RE-CENTRING" : "   pointer: twist to aim, hold minus to re-centre";
                else if (s.HasGyro) motion += string.Format("   turn {0:+000;-000} {1:+000;-000} {2:+000;-000}", s.Pitch, s.Roll, s.Yaw);
                l.Text = name + "slot " + w.Device.Slot + extras + Environment.NewLine +
                         "      Buttons: " + s.ButtonText() + "      " + motion;
                l.ForeColor = Good;
            }

            if (server == null) { xboxLine.Text = "Xbox:  -"; xboxLine.ForeColor = Ink; return; }
            var clients = server.ActiveClients();
            xboxLine.Text = clients.Count > 0
                ? "Xbox:  connected (" + string.Join(", ", clients) + ")"
                : "Xbox:  waiting. Start a Wii game in Dolphin on the Xbox.";
            xboxLine.ForeColor = clients.Count > 0 ? Good : Warn;

            if (connected == 0)
                SetStatus("Looking for remotes... press A on one.", Warn);
            else if (clients.Count > 0)
                SetStatus("WORKING! " + connected + (connected == 1 ? " remote is" : " remotes are") + " going to the Xbox.", Good);
            else
                SetStatus(connected + (connected == 1 ? " remote" : " remotes") + " connected. Waiting for the Xbox...", Good);
        }
    }

    // Test screen: one small box per player with a dot where the pointer should be.
    internal sealed class PointerPreview : Control
    {
        public Func<(bool connected, WiiState state)[]> Seats;

        public PointerPreview()
        {
            DoubleBuffered = true;
            Size = new Size(900, 150);
            Margin = new Padding(0, 4, 0, 8);
            Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.White);
            var seats = Seats?.Invoke();
            if (seats == null) return;
            int boxW = 200, boxH = 112, gap = 24, top = 28;
            for (int i = 0; i < seats.Length; i++)
            {
                int x = i * (boxW + gap);
                var box = new Rectangle(x, top, boxW, boxH);
                g.DrawString("Player " + (i + 1) + " pointer", Font, Brushes.Black, x, 2);
                using (var fill = new SolidBrush(Color.FromArgb(32, 36, 44))) g.FillRectangle(fill, box);
                using (var pen = new Pen(Color.FromArgb(80, 86, 96))) { g.DrawLine(pen, box.Left + boxW / 2, box.Top, box.Left + boxW / 2, box.Bottom); g.DrawLine(pen, box.Left, box.Top + boxH / 2, box.Right, box.Top + boxH / 2); }
                var (connected, s) = seats[i];
                string note = null;
                if (!connected || s == null) note = "no remote";
                else if (!s.TwistMode) note = s.HasGyro ? "MotionPlus" : "tilt only";
                if (note != null)
                {
                    var size = g.MeasureString(note, Font);
                    g.DrawString(note, Font, Brushes.Gainsboro, box.Left + (boxW - size.Width) / 2, box.Top + (boxH - size.Height) / 2);
                    continue;
                }
                float px = box.Left + boxW / 2f + s.PointerX * (boxW / 2f - 10);
                float py = box.Top + boxH / 2f - s.PointerY * (boxH / 2f - 10);
                using var dot = new SolidBrush(s.Recentering ? Color.Orange : Color.LimeGreen);
                g.FillEllipse(dot, px - 9, py - 9, 18, 18);
            }
        }
    }
}
