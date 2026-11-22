using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace WiiRemotePairer
{
    internal class MainForm : Form
    {
        readonly Label radioLabel = new Label();
        readonly Label status = new Label();
        readonly Label countLabel = new Label();
        readonly ListView list = new ListView();
        readonly Button autoBtn = new Button();
        readonly Button scanBtn = new Button();
        readonly Button pairBtn = new Button();
        readonly Button forgetBtn = new Button();
        readonly TextBox logBox = new TextBox();

        // Everything ever found stays here (and in the list) until "Forget" removes it.
        readonly Dictionary<ulong, ListViewItem> items = new Dictionary<ulong, ListViewItem>();
        readonly Dictionary<ulong, Bluetooth.DeviceInfo> devices = new Dictionary<ulong, Bluetooth.DeviceInfo>();
        readonly Dictionary<ulong, DateTime> lastSeen = new Dictionary<ulong, DateTime>();

        Bluetooth.Radio radio;
        Thread scanThread;
        volatile bool scanning;
        volatile bool autoRunning;
        volatile bool closing;
        bool busy;

        static readonly Font Big = new Font("Segoe UI", 16f);
        static readonly Font BigBold = new Font("Segoe UI", 16f, FontStyle.Bold);
        static readonly Font ButtonFont = new Font("Segoe UI", 18f, FontStyle.Bold);
        static readonly Font Huge = new Font("Segoe UI", 28f, FontStyle.Bold);

        static readonly Color Ink = Color.FromArgb(30, 30, 30);
        static readonly Color Good = Color.FromArgb(20, 130, 60);
        static readonly Color Warn = Color.FromArgb(200, 90, 0);
        static readonly Color Bad = Color.FromArgb(190, 30, 30);

        public MainForm()
        {
            Text = "Wii Remote Pairer";
            Font = Big;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 247);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // radio
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // big status
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // count
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); // list
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); // log
            Controls.Add(layout);

            radioLabel.AutoSize = true;
            radioLabel.Font = BigBold;
            radioLabel.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(radioLabel);

            status.AutoSize = true;
            status.Font = Huge;
            status.Margin = new Padding(0, 0, 0, 12);
            status.Text = "Click AUTO-PAIR, then press the red button on the remote.";
            status.ForeColor = Ink;
            layout.Controls.Add(status);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), WrapContents = true };
            Style(autoBtn, "AUTO-PAIR", Warn, 300);
            Style(scanBtn, "Start scanning", Color.FromArgb(32, 96, 200), 280);
            Style(pairBtn, "Pair this remote", Good, 280);
            Style(forgetBtn, "Forget this device", Color.FromArgb(150, 60, 40), 280);
            autoBtn.Click += (s, e) => ToggleAuto();
            scanBtn.Click += (s, e) => ToggleScan();
            pairBtn.Click += (s, e) => PairSelected();
            forgetBtn.Click += (s, e) => ForgetSelected();
            buttons.Controls.Add(autoBtn);
            buttons.Controls.Add(scanBtn);
            buttons.Controls.Add(pairBtn);
            buttons.Controls.Add(forgetBtn);
            layout.Controls.Add(buttons);

            countLabel.AutoSize = true;
            countLabel.Text = "Devices found: 0";
            layout.Controls.Add(countLabel);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Font = Big;
            list.Columns.Add("Name", 360);
            list.Columns.Add("Address", 250);
            list.Columns.Add("Type", 130);
            list.Columns.Add("Status", 250);
            list.Columns.Add("Last seen", 170);
            list.ListViewItemSorter = new WiiFirst(this);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            layout.Controls.Add(list);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Segoe UI", 13f);
            logBox.BackColor = Color.White;
            layout.Controls.Add(logBox);

            Load += (s, e) => FindRadio();
            FormClosing += (s, e) => { closing = true; scanning = false; autoRunning = false; };
            UpdateButtons();
        }

        static void Style(Button b, string text, Color c, int width)
        {
            b.Text = text;
            b.Size = new Size(width, 84);
            b.Font = ButtonFont;
            b.BackColor = c;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Margin = new Padding(0, 0, 14, 8);
            b.Cursor = Cursors.Hand;
        }

        // Wii-looking devices first, then the most recently seen.
        class WiiFirst : System.Collections.IComparer
        {
            readonly MainForm f;
            public WiiFirst(MainForm f) { this.f = f; }
            public int Compare(object a, object b)
            {
                ulong x = (ulong)((ListViewItem)a).Tag, y = (ulong)((ListViewItem)b).Tag;
                bool wx = f.IsCandidate(x), wy = f.IsCandidate(y);
                if (wx != wy) return wx ? -1 : 1;
                return f.lastSeen[y].CompareTo(f.lastSeen[x]);
            }
        }

        void Log(string s)
        {
            if (closing) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            logBox.AppendText(DateTime.Now.ToString("h:mm:ss tt") + "   " + s + Environment.NewLine);
        }

        void SetStatus(string s, Color c)
        {
            if (closing) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetStatus), s, c); return; }
            status.Text = s;
            status.ForeColor = c;
        }

        void FindRadio()
        {
            try { radio = Bluetooth.FindRadio(); }
            catch (Exception ex) { Log("Error finding Bluetooth: " + ex.Message); }
            if (radio == null)
            {
                radioLabel.Text = "No Bluetooth found on this PC. Is Bluetooth turned on?";
                radioLabel.ForeColor = Bad;
                SetStatus("Turn Bluetooth on, then reopen this app.", Bad);
            }
            else
            {
                radioLabel.Text = "This PC's Bluetooth:  " + Bluetooth.FormatAddress(radio.Address);
                radioLabel.ForeColor = Ink;
                Log("Bluetooth is ready. Close WiimoteHook before pairing.");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Show(Bluetooth.Scan(radio, false, 0)); } catch { }
                });
            }
            UpdateButtons();
        }

        // ---------- the list ----------

        bool IsCandidate(ulong addr)
        {
            if (!devices.TryGetValue(addr, out var d)) return false;
            if (Bluetooth.LooksLikeWiiRemote(d)) return true;
            // Copies sometimes give no name: an unnamed "peripheral" is worth a try.
            return string.IsNullOrWhiteSpace(d.szName) && Bluetooth.IsPeripheral(d);
        }

        void Show(List<Bluetooth.DeviceInfo> found)
        {
            if (closing) return;
            if (InvokeRequired) { BeginInvoke(new Action<List<Bluetooth.DeviceInfo>>(Show), found); return; }
            list.BeginUpdate();
            foreach (var d in found)
            {
                bool isNew = !devices.ContainsKey(d.Address);
                var dev = d;
                // Keep a name or type we saw before if this scan came back without one.
                if (!isNew)
                {
                    var old = devices[d.Address];
                    if (string.IsNullOrWhiteSpace(dev.szName)) dev.szName = old.szName;
                    if (dev.ulClassofDevice == 0) dev.ulClassofDevice = old.ulClassofDevice;
                }
                devices[d.Address] = dev;
                lastSeen[d.Address] = DateTime.Now;

                string name = string.IsNullOrWhiteSpace(dev.szName) ? "(no name)" : dev.szName;
                string state = dev.fConnected ? "Connected" : dev.fAuthenticated ? "Paired" : dev.fRemembered ? "Saved, not connected" : "Not paired";
                bool wii = IsCandidate(d.Address);

                if (!items.TryGetValue(d.Address, out var it))
                {
                    it = new ListViewItem(new[] { name, "", "", "", "" }) { Tag = d.Address };
                    items[d.Address] = it;
                    list.Items.Add(it);
                    Log("Found: " + name + "  " + Bluetooth.FormatAddress(dev.Address) + (wii ? "   <-- could be a Wii remote!" : ""));
                }
                it.SubItems[0].Text = name;
                it.SubItems[1].Text = Bluetooth.FormatAddress(dev.Address);
                it.SubItems[2].Text = "0x" + dev.ulClassofDevice.ToString("X6");
                it.SubItems[3].Text = state;
                it.SubItems[4].Text = DateTime.Now.ToString("h:mm:ss tt");
                it.BackColor = wii ? Color.FromArgb(200, 240, 200) : Color.White;
                it.Font = wii ? BigBold : Big;
            }
            list.Sort();
            list.EndUpdate();
            countLabel.Text = "Devices found: " + list.Items.Count;
            UpdateButtons();
        }

        bool TryGetSelected(out Bluetooth.DeviceInfo dev)
        {
            dev = default;
            if (list.SelectedItems.Count == 0) return false;
            return devices.TryGetValue((ulong)list.SelectedItems[0].Tag, out dev);
        }

        void UpdateButtons()
        {
            if (closing) return;
            bool sel = list.SelectedItems.Count > 0;
            bool ready = radio != null && !busy;
            autoBtn.Enabled = radio != null && (autoRunning || !busy);
            autoBtn.Text = autoRunning ? "STOP" : "AUTO-PAIR";
            scanBtn.Enabled = ready && !autoRunning;
            pairBtn.Enabled = ready && sel && !autoRunning;
            forgetBtn.Enabled = ready && sel && !autoRunning;
        }

        // ---------- manual scanning and pairing ----------

        void ToggleScan()
        {
            if (scanning) { StopScan(); return; }
            if (radio == null) return;
            scanning = true;
            scanBtn.Text = "Stop scanning";
            Log("Scanning... press the red button on the remote now.");
            scanThread = new Thread(() =>
            {
                while (scanning)
                {
                    try { Show(Bluetooth.Scan(radio, true, 3)); }
                    catch (Exception ex) { Log("Scan error: " + ex.Message); Thread.Sleep(1000); }
                }
            }) { IsBackground = true };
            scanThread.Start();
            UpdateButtons();
        }

        void StopScan()
        {
            scanning = false;
            scanBtn.Text = "Start scanning";
            Log("Stopped scanning.");
            UpdateButtons();
        }

        void PairSelected()
        {
            if (!TryGetSelected(out var dev)) return;
            if (scanning) StopScan();
            busy = true;
            UpdateButtons();
            string name = string.IsNullOrWhiteSpace(dev.szName) ? Bluetooth.FormatAddress(dev.Address) : dev.szName;
            SetStatus("Pairing " + name + "... keep the lights blinking.", Warn);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                scanThread?.Join(8000);
                var result = Bluetooth.PairResult.Refused;
                try { result = Bluetooth.Pair(radio, dev, Log); }
                catch (Exception ex) { Log("Error: " + ex.Message); }
                Finished(result);
                BeginInvoke(new Action(() =>
                {
                    busy = false;
                    UpdateButtons();
                    try { Show(Bluetooth.Scan(radio, false, 0)); } catch { }
                }));
            });
        }

        void Finished(Bluetooth.PairResult result)
        {
            switch (result)
            {
                case Bluetooth.PairResult.Paired:
                    SetStatus("PAIRED!  Now open WiimoteHook.", Good);
                    break;
                case Bluetooth.PairResult.RemoteWentToSleep:
                    SetStatus("The remote went to sleep. Try AUTO-PAIR.", Bad);
                    break;
                default:
                    SetStatus("It didn't pair. This remote may not work with a PC.", Bad);
                    break;
            }
        }

        void ForgetSelected()
        {
            if (!TryGetSelected(out var dev)) return;
            uint err = Bluetooth.Remove(dev.Address);
            if (err == 0)
            {
                Log("Forgot " + Bluetooth.FormatAddress(dev.Address) + ". You can pair it fresh now.");
                list.Items.Remove(items[dev.Address]);
                items.Remove(dev.Address);
                devices.Remove(dev.Address);
                lastSeen.Remove(dev.Address);
                countLabel.Text = "Devices found: " + list.Items.Count;
            }
            else
            {
                Log("Windows didn't have it saved, so there's nothing to forget. That's fine.");
            }
            UpdateButtons();
        }

        // ---------- auto-pair ----------

        void ToggleAuto()
        {
            if (autoRunning)
            {
                autoRunning = false;
                SetStatus("Stopping...", Ink);
                return;
            }
            if (radio == null) return;
            if (scanning) StopScan();
            autoRunning = true;
            busy = true;
            UpdateButtons();
            var t = new Thread(AutoPairLoop) { IsBackground = true };
            t.Start();
        }

        void AutoPairLoop()
        {
            scanThread?.Join(8000);
            Log("AUTO-PAIR started.");
            SetStatus("Press the RED BUTTON on the remote now.", Warn);

            var refused = new Dictionary<ulong, HashSet<int>>();  // ways each remote said no to
            var gaveUp = new HashSet<ulong>();
            DateTime findDeadline = DateTime.Now.AddMinutes(2);
            DateTime? retryDeadline = null;
            var final = Bluetooth.PairResult.Refused;
            bool any = false;

            try
            {
                while (autoRunning)
                {
                    if (retryDeadline == null && DateTime.Now > findDeadline)
                    {
                        SetStatus("Didn't see the remote. Click AUTO-PAIR to try again.", Bad);
                        break;
                    }
                    if (retryDeadline != null && DateTime.Now > retryDeadline)
                    {
                        Finished(final);
                        break;
                    }

                    List<Bluetooth.DeviceInfo> found;
                    try { found = Bluetooth.Scan(radio, true, 2); }
                    catch (Exception ex) { Log("Scan error: " + ex.Message); Thread.Sleep(1000); continue; }
                    Show(found);
                    if (!autoRunning) break;

                    Bluetooth.DeviceInfo? target = null;
                    foreach (var d in found)
                    {
                        if (d.fConnected || gaveUp.Contains(d.Address)) continue;
                        bool cand = Bluetooth.LooksLikeWiiRemote(d) ||
                                    (string.IsNullOrWhiteSpace(d.szName) && Bluetooth.IsPeripheral(d));
                        if (cand) { target = d; break; }
                    }
                    if (target == null)
                    {
                        if (retryDeadline != null)
                            SetStatus("PRESS THE RED BUTTON AGAIN", Bad);
                        continue;
                    }

                    var dev = target.Value;
                    any = true;
                    if (retryDeadline == null) retryDeadline = DateTime.Now.AddSeconds(30);
                    string name = string.IsNullOrWhiteSpace(dev.szName) ? Bluetooth.FormatAddress(dev.Address) : dev.szName;
                    SetStatus("Found " + name + "!  Pairing... don't touch the remote.", Good);
                    Log("AUTO-PAIR: trying " + name + ".");

                    if (!refused.TryGetValue(dev.Address, out var ways)) refused[dev.Address] = ways = new HashSet<int>();
                    final = Bluetooth.Pair(radio, dev, Log, ways);

                    if (final == Bluetooth.PairResult.Paired)
                    {
                        Finished(final);
                        break;
                    }
                    if (final == Bluetooth.PairResult.Refused)
                    {
                        Log("AUTO-PAIR: " + name + " said no to every way.");
                        gaveUp.Add(dev.Address);
                        // Keep looking in case it was the wrong device, until the 30 seconds run out.
                        SetStatus("That one said no. Press the red button again.", Bad);
                        continue;
                    }
                    // Went to sleep part-way: ask for the red button and go round again.
                    SetStatus("PRESS THE RED BUTTON AGAIN", Bad);
                    Thread.Sleep(500);
                }
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
            }

            if (!autoRunning && !closing)
                SetStatus(any ? "Stopped." : "Stopped. Click AUTO-PAIR to try again.", Ink);
            autoRunning = false;
            Log("AUTO-PAIR finished.");
            if (closing) return;
            BeginInvoke(new Action(() =>
            {
                busy = false;
                UpdateButtons();
                try { Show(Bluetooth.Scan(radio, false, 0)); } catch { }
            }));
        }
    }
}
