using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
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
        readonly Label status = new Label();
        readonly Label barLine = new Label();
        readonly Label remoteLine = new Label();
        readonly Label xboxLine = new Label();
        readonly Label buttonsLine = new Label();
        readonly Label motionLine = new Label();
        readonly Button startBtn = new Button();
        readonly Button calibrateBtn = new Button();
        readonly Button reconnectBtn = new Button();
        readonly TextBox logBox = new TextBox();

        static readonly Font Big = new Font("Segoe UI", 16f);
        static readonly Font BigBold = new Font("Segoe UI", 17f, FontStyle.Bold);
        static readonly Font Huge = new Font("Segoe UI", 28f, FontStyle.Bold);
        static readonly Color Ink = Color.FromArgb(30, 30, 30);
        static readonly Color Good = Color.FromArgb(20, 130, 60);
        static readonly Color Warn = Color.FromArgb(200, 90, 0);
        static readonly Color Bad = Color.FromArgb(190, 30, 30);

        readonly string logPath;
        readonly object logGate = new object();
        DsuServer server;
        CancellationTokenSource runCts;
        CancellationTokenSource remoteCts;
        volatile Wiimote current;
        volatile WiiState latest;
        volatile bool closing;
        string lastProbeReport = "";

        public BridgeForm()
        {
            Text = "Wii Remote Bridge";
            Font = Big;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(900, 650);
            BackColor = Color.FromArgb(250, 250, 247);

            logPath = Path.Combine(AppContext.BaseDirectory, "WiiRemoteBridge-log.txt");
            try { File.WriteAllText(logPath, "Wii Remote Bridge log, " + DateTime.Now + Environment.NewLine); }
            catch { logPath = Path.Combine(Path.GetTempPath(), "WiiRemoteBridge-log.txt"); }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(16) };
            for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            Style(reconnectBtn, "Reconnect remote", Warn, 300);
            startBtn.Click += (s, e) => ToggleStart();
            calibrateBtn.Click += (s, e) => current?.Calibrate();
            reconnectBtn.Click += (s, e) => { Log("Reconnecting the remote..."); remoteCts?.Cancel(); };
            buttons.Controls.Add(startBtn);
            buttons.Controls.Add(calibrateBtn);
            buttons.Controls.Add(reconnectBtn);
            layout.Controls.Add(buttons);

            foreach (var l in new[] { barLine, remoteLine, xboxLine, buttonsLine, motionLine })
            {
                l.AutoSize = true;
                l.Font = BigBold;
                l.Margin = new Padding(0, 4, 0, 4);
                l.MaximumSize = new Size(1800, 0);
                layout.Controls.Add(l);
            }

            var pcIp = new Label { AutoSize = true, Font = Big, Margin = new Padding(0, 4, 0, 8), Text = "This PC's address for Dolphin:  " + LocalAddresses() + "   port " + DsuServer.DefaultPort };
            layout.Controls.Add(pcIp);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Segoe UI", 12.5f);
            logBox.BackColor = Color.White;
            layout.Controls.Add(logBox);

            SetStatus("Click START.", Ink);
            barLine.Text = "Dolphin Bar:  -";
            remoteLine.Text = "Remote:  -";
            xboxLine.Text = "Xbox:  -";
            buttonsLine.Text = "Buttons:  -";
            motionLine.Text = "Motion:  -";
            calibrateBtn.Enabled = reconnectBtn.Enabled = false;

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

        void Ui(Action a)
        {
            if (closing) return;
            try { BeginInvoke(a); } catch { }
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

            server = new DsuServer(Log) { RemoteConnected = () => current != null && latest != null };
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
            reconnectBtn.Enabled = true;
            var token = runCts.Token;
            _ = Task.Run(() => RemoteLoop(token));
        }

        async Task RemoteLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var wiimote = await FindRemote(ct).ConfigureAwait(false);
                    if (wiimote == null)
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }
                    var (w, dev) = wiimote.Value;
                    current = w;
                    w.StateChanged += s => { latest = s; server?.SendState(s); };
                    SetStatus("Remote connected! Waiting for the Xbox...", Good);
                    Ui(() => calibrateBtn.Enabled = true);

                    remoteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await w.RunAsync(remoteCts.Token).ConfigureAwait(false);

                    current = null;
                    latest = null;
                    dev.Dispose();
                    Ui(() => calibrateBtn.Enabled = false);
                    if (!ct.IsCancellationRequested)
                    {
                        SetStatus("Lost the remote. Press A on it to wake it up.", Bad);
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log("Problem: " + ex.Message);
                    try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { return; }
                }
            }
        }

        async Task<(Wiimote, HidDevice)?> FindRemote(CancellationToken ct)
        {
            var devices = HidDevice.FindWiimotes();
            if (devices.Count == 0)
            {
                SetLine(barLine, "Dolphin Bar:  not found. Is it plugged in and on mode 4?", Bad);
                SetLine(remoteLine, "Remote:  -", Ink);
                SetStatus("Plug in the Dolphin Bar and set it to mode 4.", Bad);
                LogOnce("No Nintendo devices found (looking for 057E:0306 / 057E:0330).");
                return null;
            }

            SetLine(barLine, "Dolphin Bar:  found (" + devices.Count + (devices.Count == 1 ? " slot" : " slots") + ")", Good);
            SetStatus("Looking for the remote... press A on it, or the bar's SYNC then the red button.", Warn);

            var report = new StringBuilder();
            report.AppendLine("Checked " + devices.Count + " Nintendo device(s):");
            foreach (var d in devices)
            {
                if (ct.IsCancellationRequested) return null;
                report.AppendLine(" Slot " + d.Slot + " (product " + d.Pid.ToString("X4") + ", in " + d.InLen + " / out " + d.OutLen + " bytes):");
                if (!d.Open(out string openErr))
                {
                    report.AppendLine("   couldn't open: " + openErr);
                    continue;
                }
                int method = await Wiimote.ProbeAsync(d, report).ConfigureAwait(false);
                if (method >= 0)
                {
                    d.Method = method;
                    report.AppendLine("   ANSWERED using " + HidDevice.MethodNames[method]);
                    LogOnce(report.ToString().TrimEnd());
                    Log("Remote found in slot " + d.Slot + ".");
                    SetLine(remoteLine, "Remote:  connected (slot " + d.Slot + ")", Good);
                    return (new Wiimote(d, Log), d);
                }
                d.Dispose();
            }
            LogOnce(report.ToString().TrimEnd());
            SetLine(remoteLine, "Remote:  not answering. Press A on it (one light should stay on).", Bad);
            return null;
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
            if (server == null) return;
            var clients = server.ActiveClients();
            var w = current;
            var s = latest;

            if (clients.Count > 0)
                xboxLine.Text = "Xbox:  connected (" + string.Join(", ", clients) + ")";
            else
                xboxLine.Text = "Xbox:  waiting. Start a Wii game in Dolphin on the Xbox.";
            xboxLine.ForeColor = clients.Count > 0 ? Good : Warn;

            if (w != null && s != null)
            {
                string extras = w.HasMotionPlus ? "  + MotionPlus" : "";
                extras += w.HasNunchuk ? "  + Nunchuk" : "";
                remoteLine.Text = remoteLine.Text.Split('+')[0].TrimEnd() + extras;
                buttonsLine.Text = "Buttons:  " + s.ButtonText();
                buttonsLine.ForeColor = s.Buttons != 0 || s.C || s.Z ? Good : Ink;
                motionLine.Text = (s.HasAccel
                        ? string.Format("Motion:  tilt {0:+0.0;-0.0} {1:+0.0;-0.0} {2:+0.0;-0.0}", s.Ax, s.Ay, s.Az)
                        : "Motion:  no tilt data yet (remote sends 0x" + s.ReportId.ToString("X2") + "), fixing...")
                    + (s.HasGyro ? string.Format("    turn {0:+000;-000} {1:+000;-000} {2:+000;-000}", s.Pitch, s.Roll, s.Yaw) : "");
                if (clients.Count > 0) SetStatus("WORKING! The remote is going to the Xbox.", Good);
                else if (status.ForeColor != Good) SetStatus("Remote connected! Waiting for the Xbox...", Good);
                else if (status.Text.StartsWith("WORKING")) SetStatus("Remote connected! Waiting for the Xbox...", Good);
            }
            else
            {
                buttonsLine.Text = "Buttons:  -";
                buttonsLine.ForeColor = Ink;
                motionLine.Text = "Motion:  -";
            }
        }
    }
}
