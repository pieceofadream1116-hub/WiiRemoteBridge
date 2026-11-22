using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WiiRemotePairer
{
    // Thin wrapper over the Windows Bluetooth API (bthprops.cpl).
    // Pairing works the same way as the WiiPair tools:
    //   red sync button  -> PIN is this PC's Bluetooth address (6 raw bytes)
    //   buttons 1 + 2    -> PIN is the remote's own address
    //   some copies      -> no PIN at all, just switch on the HID service
    internal static class Bluetooth
    {
        const string Lib = "bthprops.cpl";

        [StructLayout(LayoutKind.Sequential)]
        struct FindRadioParams { public uint dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RadioInfo
        {
            public uint dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SearchParams
        {
            public uint dwSize;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SystemTime { public ushort y, mo, dow, d, h, mi, s, ms; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DeviceInfo
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            SystemTime stLastSeen;
            SystemTime stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport(Lib, SetLastError = true)]
        static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr hRadio);
        [DllImport(Lib, SetLastError = true)]
        static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr hRadio);
        [DllImport(Lib, SetLastError = true)]
        static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport(Lib, SetLastError = true)]
        static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref RadioInfo info);
        [DllImport(Lib, SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo info);
        [DllImport(Lib, SetLastError = true)]
        static extern bool BluetoothFindNextDevice(IntPtr hFind, ref DeviceInfo info);
        [DllImport(Lib, SetLastError = true)]
        static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport(Lib, SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint BluetoothAuthenticateDevice(IntPtr hwndParent, IntPtr hRadio, ref DeviceInfo info,
            [In, MarshalAs(UnmanagedType.LPArray)] char[] passkey, uint passkeyLength);
        [DllImport(Lib, SetLastError = true)]
        static extern uint BluetoothSetServiceState(IntPtr hRadio, ref DeviceInfo info, ref Guid service, uint flags);
        [DllImport(Lib, SetLastError = true)]
        static extern uint BluetoothRemoveDevice(ref ulong address);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        static Guid HidService = new Guid("00001124-0000-1000-8000-00805f9b34fb");
        const uint ServiceEnable = 1;

        internal class Radio
        {
            public IntPtr Handle;
            public ulong Address;
            public string Name;
        }

        public static Radio FindRadio()
        {
            var p = new FindRadioParams { dwSize = (uint)Marshal.SizeOf<FindRadioParams>() };
            IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr h);
            if (find == IntPtr.Zero) return null;
            BluetoothFindRadioClose(find);
            var info = new RadioInfo { dwSize = (uint)Marshal.SizeOf<RadioInfo>() };
            BluetoothGetRadioInfo(h, ref info);
            return new Radio { Handle = h, Address = info.address, Name = info.szName };
        }

        static DeviceInfo NewInfo() => new DeviceInfo { dwSize = (uint)Marshal.SizeOf<DeviceInfo>(), szName = "" };

        // One scan. inquiry=true asks the radio to look for new devices (about 1.28s per timeout unit).
        public static List<DeviceInfo> Scan(Radio r, bool inquiry, byte timeout)
        {
            var list = new List<DeviceInfo>();
            var sp = new SearchParams
            {
                dwSize = (uint)Marshal.SizeOf<SearchParams>(),
                fReturnAuthenticated = true,
                fReturnRemembered = true,
                fReturnUnknown = true,
                fReturnConnected = true,
                fIssueInquiry = inquiry,
                cTimeoutMultiplier = timeout,
                hRadio = r.Handle,
            };
            var info = NewInfo();
            IntPtr find = BluetoothFindFirstDevice(ref sp, ref info);
            if (find == IntPtr.Zero) return list;
            do
            {
                list.Add(info);
                info = NewInfo();
            } while (BluetoothFindNextDevice(find, ref info));
            BluetoothFindDeviceClose(find);
            return list;
        }

        static char[] Pin(ulong address)
        {
            var b = BitConverter.GetBytes(address); // little-endian: same order as rgBytes
            var pin = new char[6];
            for (int i = 0; i < 6; i++) pin[i] = (char)b[i];
            return pin;
        }

        public static uint Remove(ulong address) => BluetoothRemoveDevice(ref address);

        // Tries the three ways in turn. Reports each attempt through log().
        public static bool Pair(Radio r, DeviceInfo dev, Action<string> log)
        {
            if (dev.fRemembered && !dev.fConnected)
            {
                log("Clearing an old pairing for this device first.");
                Remove(dev.Address);
            }

            var attempts = new (string what, char[] pin)[]
            {
                ("Way 1: red sync button (PIN = this PC's address)", Pin(r.Address)),
                ("Way 2: buttons 1 + 2 (PIN = the remote's address)", Pin(dev.Address)),
                ("Way 3: no PIN", null),
            };

            foreach (var (what, pin) in attempts)
            {
                log("Trying " + what + " ...");
                var d = dev;
                uint err = 0;
                if (pin != null)
                {
                    err = BluetoothAuthenticateDevice(IntPtr.Zero, r.Handle, ref d, pin, 6);
                    if (err != 0)
                    {
                        log("  No luck (" + Explain(err) + ").");
                        continue;
                    }
                }
                err = BluetoothSetServiceState(r.Handle, ref d, ref HidService, ServiceEnable);
                if (err == 0)
                {
                    log("  It worked!");
                    return true;
                }
                log("  Paired, but the controller part didn't switch on (" + Explain(err) + ").");
                if (pin != null) Remove(dev.Address);
            }
            return false;
        }

        public static string Explain(uint err)
        {
            switch (err)
            {
                case 0: return "OK";
                case 5: return "access denied - try running as administrator";
                case 87: return "the remote didn't answer in time";
                case 1167: return "the remote isn't listening - press its red button again";
                case 1168: return "the remote has gone - press its red button again";
                case 1223: return "it was cancelled";
                case 1244: return "the PIN was not accepted";
                case 1460: return "it timed out";
                default: return "code " + err;
            }
        }

        public static string FormatAddress(ulong a)
        {
            var b = BitConverter.GetBytes(a);
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}", b[5], b[4], b[3], b[2], b[1], b[0]);
        }

        // Real Wii Remotes report class 0x002504 (Peripheral / Joystick); the newer "Plus" ones 0x000508.
        public static bool LooksLikeWiiRemote(DeviceInfo d)
        {
            string n = (d.szName ?? "").ToUpperInvariant();
            if (n.Contains("NINTENDO") || n.Contains("RVL") || n.Contains("WII") || n.Contains("MOTION")) return true;
            uint c = d.ulClassofDevice & 0xFFFFFC;
            return c == 0x002504 || c == 0x000508 || c == 0x000504 || c == 0x002508;
        }
    }
}
