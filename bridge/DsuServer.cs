using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WiiRemoteBridge
{
    // A "DSU" (Cemuhook) motion server: the same thing WiimoteHook provided.
    // Dolphin on the Xbox asks this PC on port 26760 and gets the remote's buttons and motion,
    // laid out like a PS4 pad so the existing "WiimoteHook" Dolphin profile works unchanged.
    internal sealed class DsuServer : IDisposable
    {
        public const int DefaultPort = 26760;
        const ushort ProtocolVersion = 1001;
        const uint TypeVersion = 0x100000, TypePorts = 0x100001, TypeData = 0x100002;

        readonly Action<string> log;
        readonly uint serverId = (uint)new Random().Next();
        readonly Dictionary<IPEndPoint, DateTime> clients = new Dictionary<IPEndPoint, DateTime>();
        readonly object gate = new object();
        UdpClient udp;
        CancellationTokenSource cts;
        uint packetCount;

        public Func<bool> RemoteConnected = () => false;
        public long PacketsSent { get; private set; }
        public long Requests { get; private set; }

        public DsuServer(Action<string> log) { this.log = log; }

        public bool Start(int port, out string error)
        {
            error = null;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                // Don't let "port unreachable" replies from a client kill the socket.
                const int SIO_UDP_CONNRESET = -1744830452;
                try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                error = "Port " + port + " is already in use. Close WiimoteHook (and any other copy of this app), then click START again.";
                udp?.Dispose(); udp = null;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                udp?.Dispose(); udp = null;
                return false;
            }
            cts = new CancellationTokenSource();
            _ = ReceiveLoop(cts.Token);
            return true;
        }

        // Who is asking for data right now (the Xbox).
        public List<string> ActiveClients()
        {
            var list = new List<string>();
            lock (gate)
                foreach (var kv in clients)
                    if ((DateTime.Now - kv.Value).TotalSeconds < 5) list.Add(kv.Key.Address.ToString());
            return list;
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                try { Handle(res.Buffer, res.RemoteEndPoint); }
                catch (Exception ex) { log("Bad message from " + res.RemoteEndPoint + ": " + ex.Message); }
            }
        }

        void Handle(byte[] m, IPEndPoint from)
        {
            if (m.Length < 20 || m[0] != 'D' || m[1] != 'S' || m[2] != 'U' || m[3] != 'C') return;
            uint type = BitConverter.ToUInt32(m, 16);
            Requests++;
            switch (type)
            {
                case TypeVersion:
                {
                    var p = NewPacket(24, TypeVersion);
                    BitConverter.GetBytes(ProtocolVersion).CopyTo(p, 20);
                    Send(p, from);
                    break;
                }
                case TypePorts:
                {
                    int count = m.Length >= 24 ? BitConverter.ToInt32(m, 20) : 0;
                    for (int i = 0; i < count && i < 4 && 24 + i < m.Length; i++)
                    {
                        var p = NewPacket(32, TypePorts);
                        WriteSharedStart(p, m[24 + i]);
                        Send(p, from);
                    }
                    break;
                }
                case TypeData:
                {
                    bool isNew;
                    lock (gate)
                    {
                        isNew = !clients.ContainsKey(from) || (DateTime.Now - clients[from]).TotalSeconds > 5;
                        clients[from] = DateTime.Now;
                    }
                    if (isNew) log("Dolphin is connected from " + from.Address + ".");
                    break;
                }
            }
        }

        // pad id, state, model, connection, MAC, battery (bytes 20-30)
        void WriteSharedStart(byte[] p, byte padId)
        {
            bool on = padId == 0 && RemoteConnected();
            p[20] = padId;
            p[21] = (byte)(on ? 2 : 0);   // connected / not
            p[22] = (byte)(on ? 2 : 0);   // full gyro
            p[23] = (byte)(on ? 2 : 0);   // bluetooth
            if (on) { p[24] = 0x00; p[25] = 0x19; p[26] = 0x1D; p[27] = 0x00; p[28] = 0x00; p[29] = 0x01; }
            p[30] = (byte)(on ? 0x05 : 0x00); // battery full
        }

        byte[] NewPacket(int size, uint type)
        {
            var p = new byte[size];
            p[0] = (byte)'D'; p[1] = (byte)'S'; p[2] = (byte)'U'; p[3] = (byte)'S';
            BitConverter.GetBytes(ProtocolVersion).CopyTo(p, 4);
            BitConverter.GetBytes((ushort)(size - 16)).CopyTo(p, 6);
            BitConverter.GetBytes(serverId).CopyTo(p, 12);
            BitConverter.GetBytes(type).CopyTo(p, 16);
            return p;
        }

        void Send(byte[] p, IPEndPoint to)
        {
            p[8] = p[9] = p[10] = p[11] = 0;
            BitConverter.GetBytes(Crc32(p)).CopyTo(p, 8);
            try { udp?.Send(p, p.Length, to); PacketsSent++; } catch { }
        }

        // Sends the remote's state to everyone who asked in the last 5 seconds.
        public void SendState(WiiState s)
        {
            List<IPEndPoint> targets;
            lock (gate)
            {
                targets = new List<IPEndPoint>();
                foreach (var kv in clients)
                    if ((DateTime.Now - kv.Value).TotalSeconds < 5) targets.Add(kv.Key);
            }
            if (targets.Count == 0) return;

            var p = NewPacket(100, TypeData);
            WriteSharedStart(p, 0);
            p[31] = 1; // active
            BitConverter.GetBytes(packetCount++).CopyTo(p, 32);

            byte b1 = 0, b2 = 0;
            if (s.Pressed(WiiState.Up)) b1 |= 0x10;
            if (s.Pressed(WiiState.Right)) b1 |= 0x20;
            if (s.Pressed(WiiState.Down)) b1 |= 0x40;
            if (s.Pressed(WiiState.Left)) b1 |= 0x80;
            if (s.Pressed(WiiState.Minus)) b1 |= 0x01; // Share
            if (s.Pressed(WiiState.Plus)) b1 |= 0x08;  // Options
            if (s.HasNunchuk && s.C) b1 |= 0x02;       // L3
            if (s.HasNunchuk && s.Z) b1 |= 0x04;       // R3
            if (s.Pressed(WiiState.A)) b2 |= 0x40;     // Cross
            if (s.Pressed(WiiState.B)) b2 |= 0x08;     // R1
            if (s.Pressed(WiiState.One)) b2 |= 0x80;   // Square
            if (s.Pressed(WiiState.Two)) b2 |= 0x20;   // Circle
            if (s.Pressed(WiiState.Home)) b2 |= 0x10;  // Triangle
            p[36] = b1;
            p[37] = b2;
            p[38] = 0; // PS
            p[39] = 0; // touch
            p[40] = s.HasNunchuk ? s.StickX : (byte)128;
            p[41] = s.HasNunchuk ? s.StickY : (byte)128;
            p[42] = 128;
            p[43] = 128;
            p[44] = Full(b1, 0x80); p[45] = Full(b1, 0x40); p[46] = Full(b1, 0x20); p[47] = Full(b1, 0x10);
            p[48] = Full(b2, 0x80); p[49] = Full(b2, 0x40); p[50] = Full(b2, 0x20); p[51] = Full(b2, 0x10);
            p[52] = Full(b2, 0x08); p[53] = Full(b2, 0x04); p[54] = 0; p[55] = 0;
            // 56-67: touch points (unused)
            BitConverter.GetBytes((ulong)s.TimestampUs).CopyTo(p, 68);

            // Dolphin turns these back into the remote's own axes:
            //   remote X = accel X,  remote Y = -accel Z,  remote Z = -accel Y
            //   remote pitch/roll/yaw = -gyro pitch/roll/yaw
            BitConverter.GetBytes(s.Ax).CopyTo(p, 76);
            BitConverter.GetBytes(-s.Az).CopyTo(p, 80);
            BitConverter.GetBytes(-s.Ay).CopyTo(p, 84);
            if (s.HasGyro)
            {
                BitConverter.GetBytes(-s.Pitch).CopyTo(p, 88);
                BitConverter.GetBytes(-s.Yaw).CopyTo(p, 92);
                BitConverter.GetBytes(-s.Roll).CopyTo(p, 96);
            }

            foreach (var t in targets) Send((byte[])p.Clone(), t);
        }

        static byte Full(byte bits, byte mask) => (byte)((bits & mask) != 0 ? 255 : 0);

        static readonly uint[] CrcTable = MakeTable();
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            try { udp?.Dispose(); } catch { }
            udp = null;
        }
    }
}
