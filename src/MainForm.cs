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
        readonly Label steps = new Label();
        readonly ListView list = new ListView();
        readonly Button scanBtn = new Button();
        readonly Button pairBtn = new Button();
        readonly Button forgetBtn = new Button();
        readonly TextBox logBox = new TextBox();

        readonly Dictionary<ulong, ListViewItem> items = new Dictionary<ulong, ListViewItem>();
        readonly Dictionary<ulong, Bluetooth.DeviceInfo> devices = new Dictionary<ulong, Bluetooth.DeviceInfo>();

        Bluetooth.Radio radio;
        Thread scanThread;
        volatile bool scanning;
        bool busy;

        static readonly Font Big = new Font("Segoe UI", 16f);
        static readonly Font Bigger = new Font("Segoe UI", 20f, FontStyle.Bold);

        public MainForm()
        {
            Text = "Wii Remote Pairer";
            Font = Big;
            ClientSize = new Size(1100, 860);
            MinimumSize = new Size(900, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 247);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            Controls.Add(layout);

            radioLabel.AutoSize = true;
            radioLabel.Font = Bigger;
            radioLabel.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(radioLabel);

            steps.AutoSize = true;
            steps.MaximumSize = new Size(1060, 0);
            steps.Margin = new Padding(0, 0, 0, 12);
            steps.Text =
                "1.  Click  Start scanning.\n" +
                "2.  Open the battery cover. Press the small red button. The lights blink.\n" +
                "3.  When the remote shows in GREEN, click it.\n" +
                "4.  Click  Pair this remote  while the lights are still blinking.";
            layout.Controls.Add(steps);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Font = Big;
            list.Columns.Add("Name", 380);
            list.Columns.Add("Address", 260);
            list.Columns.Add("Type", 140);
            list.Columns.Add("Status", 260);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            layout.Controls.Add(list);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12) };
            Style(scanBtn, "Start scanning", Color.FromArgb(32, 96, 200));
            Style(pairBtn, "Pair this remote", Color.FromArgb(20, 130, 60));
            Style(forgetBtn, "Forget this device", Color.FromArgb(150, 60, 40));
            scanBtn.Click += (s, e) => ToggleScan();
            pairBtn.Click += (s, e) => PairSelected();
            forgetBtn.Click += (s, e) => ForgetSelected();
            buttons.Controls.Add(scanBtn);
            buttons.Controls.Add(pairBtn);
            buttons.Controls.Add(forgetBtn);
            layout.Controls.Add(buttons);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Segoe UI", 14f);
            logBox.BackColor = Color.White;
            layout.Controls.Add(logBox);

            Load += (s, e) => FindRadio();
            FormClosing += (s, e) => scanning = false;
            UpdateButtons();
        }

        static void Style(Button b, string text, Color c)
        {
            b.Text = text;
            b.Size = new Size(330, 90);
            b.Font = Bigger;
            b.BackColor = c;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Margin = new Padding(0, 0, 16, 0);
            b.Cursor = Cursors.Hand;
        }

        void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            logBox.AppendText(DateTime.Now.ToString("h:mm:ss tt") + "   " + s + Environment.NewLine);
        }

        void FindRadio()
        {
            try { radio = Bluetooth.FindRadio(); }
            catch (Exception ex) { Log("Error finding Bluetooth: " + ex.Message); }
            if (radio == null)
            {
                radioLabel.Text = "No Bluetooth found on this PC. Is Bluetooth turned on?";
                radioLabel.ForeColor = Color.DarkRed;
                Log("Turn Bluetooth on in Windows Settings, then close and reopen this app.");
            }
            else
            {
                radioLabel.Text = "This PC's Bluetooth:  " + Bluetooth.FormatAddress(radio.Address);
                radioLabel.ForeColor = Color.FromArgb(30, 30, 30);
                Log("Bluetooth is ready. Close WiimoteHook before pairing.");
                // Show what Windows already knows about, without a scan.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Show(Bluetooth.Scan(radio, false, 0)); } catch { }
                });
            }
            UpdateButtons();
        }

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
                    try { Show(Bluetooth.Scan(radio, true, 4)); }
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

        void Show(List<Bluetooth.DeviceInfo> found)
        {
            if (InvokeRequired) { BeginInvoke(new Action<List<Bluetooth.DeviceInfo>>(Show), found); return; }
            foreach (var d in found)
            {
                bool isNew = !items.ContainsKey(d.Address);
                var old = isNew ? default : devices[d.Address];
                // Keep a name we saw before if this scan came back without one.
                var dev = d;
                if (string.IsNullOrWhiteSpace(dev.szName) && !isNew) dev.szName = old.szName;
                devices[d.Address] = dev;

                string name = string.IsNullOrWhiteSpace(dev.szName) ? "(no name)" : dev.szName;
                string status = dev.fConnected ? "Connected" : dev.fAuthenticated ? "Paired" : dev.fRemembered ? "Saved, not connected" : "Not paired";
                string type = "0x" + dev.ulClassofDevice.ToString("X6");
                bool wii = Bluetooth.LooksLikeWiiRemote(dev);

                ListViewItem it;
                if (isNew)
                {
                    it = new ListViewItem(new[] { name, Bluetooth.FormatAddress(dev.Address), type, status });
                    items[d.Address] = it;
                    list.Items.Add(it);
                    Log("Found: " + name + "  " + Bluetooth.FormatAddress(dev.Address) + (wii ? "   <-- looks like a Wii remote!" : ""));
                }
                else
                {
                    it = items[d.Address];
                    it.SubItems[0].Text = name;
                    it.SubItems[2].Text = type;
                    it.SubItems[3].Text = status;
                }
                it.BackColor = wii ? Color.FromArgb(200, 240, 200) : Color.White;
                it.Font = wii ? new Font(Big, FontStyle.Bold) : Big;
            }
            UpdateButtons();
        }

        bool TryGetSelected(out Bluetooth.DeviceInfo dev)
        {
            dev = default;
            if (list.SelectedItems.Count == 0) return false;
            foreach (var kv in items)
                if (kv.Value == list.SelectedItems[0]) { dev = devices[kv.Key]; return true; }
            return false;
        }

        void UpdateButtons()
        {
            bool sel = list.SelectedItems.Count > 0;
            scanBtn.Enabled = radio != null && !busy;
            pairBtn.Enabled = sel && !busy && radio != null;
            forgetBtn.Enabled = sel && !busy && radio != null;
        }

        void PairSelected()
        {
            if (!TryGetSelected(out var dev)) return;
            if (scanning) { scanning = false; scanBtn.Text = "Start scanning"; }
            busy = true;
            UpdateButtons();
            string name = string.IsNullOrWhiteSpace(dev.szName) ? Bluetooth.FormatAddress(dev.Address) : dev.szName;
            Log("Pairing " + name + ". Keep the lights blinking (press red again if they stop).");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Let any scan that is still running finish first.
                scanThread?.Join(8000);
                bool ok = false;
                try { ok = Bluetooth.Pair(radio, dev, Log); }
                catch (Exception ex) { Log("Error: " + ex.Message); }
                if (ok)
                    Log("PAIRED! Now open WiimoteHook. The remote should show up there.");
                else
                    Log("It didn't pair. Press the red button again and click Pair this remote once more. " +
                        "If it never works, this remote may not be able to pair with a PC.");
                BeginInvoke(new Action(() =>
                {
                    busy = false;
                    UpdateButtons();
                    try { Show(Bluetooth.Scan(radio, false, 0)); } catch { }
                }));
            });
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
            }
            else
            {
                Log("Windows didn't have it saved (" + Bluetooth.Explain(err) + "). That's fine.");
            }
            UpdateButtons();
        }
    }
}
