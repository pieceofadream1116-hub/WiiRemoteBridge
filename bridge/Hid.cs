using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WiiRemoteBridge
{
    // A Wii Remote seen by Windows as a HID device: either through the Dolphin Bar (mode 4),
    // which shows up as four Nintendo devices (one per slot), or paired straight over Bluetooth.
    internal sealed class HidDevice : IDisposable
    {
        public const ushort Nintendo = 0x057E;
        public const ushort WiimotePid = 0x0306;     // original remote, and what the Dolphin Bar reports
        public const ushort WiimotePlusPid = 0x0330; // Wii Remote Plus (-TR)

        public string Path;
        public ushort Pid;
        public int InLen;
        public int OutLen;
        public int Slot; // 1-based order among the Nintendo devices found

        // Which of the four ways of sending worked (-1 = not known yet)
        public int Method = -1;
        public static readonly string[] MethodNames =
        {
            "WriteFile (exact size)",
            "WriteFile (padded)",
            "SetOutputReport (exact size)",
            "SetOutputReport (padded)",
        };

        SafeFileHandle readHandle, writeHandle;
        FileStream reader, writer;

        // ---------- finding devices ----------

        public static List<HidDevice> FindWiimotes()
        {
            var found = new List<HidDevice>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return found;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data)) break;
                    string path = GetPath(set, ref data);
                    if (path == null) continue;

                    using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        if (attr.VendorID != Nintendo) continue;
                        if (attr.ProductID != WiimotePid && attr.ProductID != WiimotePlusPid) continue;

                        int inLen = 22, outLen = 22;
                        if (HidD_GetPreparsedData(h, out IntPtr pd))
                        {
                            if (HidP_GetCaps(pd, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS)
                            {
                                if (caps.InputReportByteLength > 0) inLen = caps.InputReportByteLength;
                                if (caps.OutputReportByteLength > 0) outLen = caps.OutputReportByteLength;
                            }
                            HidD_FreePreparsedData(pd);
                        }
                        found.Add(new HidDevice { Path = path, Pid = attr.ProductID, InLen = inLen, OutLen = outLen });
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            for (int i = 0; i < found.Count; i++) found[i].Slot = i + 1;
            return found;
        }

        static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
            if (needed <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, needed, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------- opening ----------

        // Two handles: one only reads, one only writes, so a waiting read never blocks a write.
        public bool Open(out string error)
        {
            error = null;
            readHandle = CreateFile(Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (readHandle.IsInvalid) { error = Win32Text(Marshal.GetLastWin32Error()); Dispose(); return false; }
            writeHandle = CreateFile(Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (writeHandle.IsInvalid) { error = Win32Text(Marshal.GetLastWin32Error()); Dispose(); return false; }
            try
            {
                reader = new FileStream(readHandle, FileAccess.ReadWrite, 0, true);
                writer = new FileStream(writeHandle, FileAccess.ReadWrite, 0, true);
            }
            catch (Exception ex) { error = ex.Message; Dispose(); return false; }
            return true;
        }

        // ---------- sending ----------

        // Sends one output report (first byte = report ID). Returns null if it worked, or why not.
        readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);

        public async Task<string> WriteAsync(byte[] report, int method, int timeoutMs = 1000)
        {
            if (!await writeLock.WaitAsync(timeoutMs * 2).ConfigureAwait(false)) return "busy";
            try { return await WriteOnce(report, method, timeoutMs).ConfigureAwait(false); }
            finally { writeLock.Release(); }
        }

        async Task<string> WriteOnce(byte[] report, int method, int timeoutMs)
        {
            if (writer == null) return "not open";
            var buf = report;
            bool padded = method == 1 || method == 3;
            if (padded && report.Length < OutLen)
            {
                buf = new byte[OutLen];
                Array.Copy(report, buf, report.Length);
            }
            try
            {
                if (method <= 1)
                {
                    using var cts = new CancellationTokenSource(timeoutMs);
                    await writer.WriteAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                    return null;
                }
                var h = writeHandle;
                var t = Task.Run(() => HidD_SetOutputReport(h, buf, buf.Length) ? 0 : Marshal.GetLastWin32Error());
                if (await Task.WhenAny(t, Task.Delay(timeoutMs)).ConfigureAwait(false) != t) return "timed out";
                return t.Result == 0 ? null : Win32Text(t.Result);
            }
            catch (OperationCanceledException) { return "timed out"; }
            catch (IOException ex) { return Win32Text(ex.HResult & 0xFFFF) + " (" + ex.Message.Trim() + ")"; }
            catch (Exception ex) { return ex.Message; }
        }

        public Task<string> WriteAsync(byte[] report) => WriteAsync(report, Method < 0 ? 0 : Method);

        // ---------- receiving ----------

        // One input report, or null if nothing came in time. Throws if the device has gone.
        public async Task<byte[]> ReadAsync(int timeoutMs)
        {
            var buf = new byte[InLen];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                int n = await reader.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                return n > 0 ? buf : null;
            }
            catch (OperationCanceledException) { return null; }
        }

        public void Dispose()
        {
            try { reader?.Dispose(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { readHandle?.Dispose(); } catch { }
            try { writeHandle?.Dispose(); } catch { }
            reader = writer = null;
        }

        public static string Win32Text(int code)
        {
            if (code == 0) return "OK";
            string text;
            try { text = new Win32Exception(code).Message; } catch { text = ""; }
            return "error " + code + (string.IsNullOrWhiteSpace(text) ? "" : ": " + text.Trim().TrimEnd('.'));
        }

        // ---------- Windows API ----------

        const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pd);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pd, out HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
    }
}
