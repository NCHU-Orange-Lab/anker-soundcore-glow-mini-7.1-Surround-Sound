using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundcoreDesk
{
    public sealed class BluetoothDevice
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public bool Connected { get; set; }
        public Guid ContainerId { get; set; }
        public BatteryReading Battery { get; set; }
    }

    public sealed class BatteryReading
    {
        public int Percentage { get; set; }
        public DateTime? ReportedAtUtc { get; set; }
        public bool? Charging { get; set; }
        public string SourceDeviceId { get; set; }

        public static BatteryReading Parse(byte[] level, uint levelType, byte[] reported, uint reportedType,
            byte[] charging, uint chargingType, string source)
        {
            // Windows' HFP battery report is a percentage byte, not a power-management state.
            if (levelType != 3 || level == null || level.Length != 1 || level[0] > 100) return null;
            DateTime? timestamp = null;
            if (reportedType == 16 && reported != null && reported.Length == 8)
            {
                try { long ticks = BitConverter.ToInt64(reported, 0); if (ticks > 0) timestamp = DateTime.FromFileTimeUtc(ticks); }
                catch (ArgumentOutOfRangeException) { }
            }
            bool? isCharging = null;
            // Only the documented ChargingState property may establish charging (0/1; 2 = unknown).
            if (chargingType == 3 && charging != null && charging.Length == 1 && charging[0] <= 1) isCharging = charging[0] == 1;
            return new BatteryReading { Percentage = level[0], ReportedAtUtc = timestamp, Charging = isCharging, SourceDeviceId = source };
        }
        public static void Attach(List<BluetoothDevice> devices, Guid container, BatteryReading candidate)
        {
            if (container == Guid.Empty || candidate == null) return;
            foreach (var device in devices)
            {
                if (device.ContainerId != container) continue;
                // A device may expose several service nodes: use the newest actual device report.
                if (device.Battery == null || (candidate.ReportedAtUtc ?? DateTime.MinValue) > (device.Battery.ReportedAtUtc ?? DateTime.MinValue)) device.Battery = candidate;
            }
        }
    }

    public static class Bluetooth
    {
        public static List<BluetoothDevice> Devices()
        {
            var result = new List<BluetoothDevice>();
            var search = new SearchParams { Size = Marshal.SizeOf(typeof(SearchParams)),
                Authenticated = true, Remembered = true, Unknown = true, Connected = true };
            var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
            IntPtr handle = BluetoothFindFirstDevice(ref search, ref info);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 259 && error != 0 && error != 1168) throw new Win32Exception(error);
                return result;
            }
            try
            {
                do
                {
                    string hex = info.Address.ToString("X12");
                    var parts = new string[6];
                    for (int i = 0; i < 6; i++) parts[i] = hex.Substring(i * 2, 2);
                    result.Add(new BluetoothDevice { Name = info.Name, Address = string.Join(":", parts), Connected = info.Connected });
                    info.Size = Marshal.SizeOf(typeof(DeviceInfo));
                } while (BluetoothFindNextDevice(handle, ref info));
                int error = Marshal.GetLastWin32Error();
                if (error != 259 && error != 0) throw new Win32Exception(error);
            }
            finally { BluetoothFindDeviceClose(handle); }
            MapContainers(result);
            return result;
        }

        static void MapContainers(List<BluetoothDevice> devices)
        {
            // HFP battery-bearing nodes may belong to System rather than the Bluetooth setup class.
            Guid anyClass = Guid.Empty;
            IntPtr set = SetupDiGetClassDevs(ref anyClass, null, IntPtr.Zero, 6); // PRESENT | ALLCLASSES
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var batteryNodes = new List<Tuple<Guid, BatteryReading>>();
                var data = new DevInfo { Size = Marshal.SizeOf(typeof(DevInfo)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var id = new StringBuilder(1024); uint required;
                    if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out required)) continue;
                    string instance = id.ToString();
                    if (!instance.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = new PropertyKey("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
                    uint type; byte[] bytes = new byte[16];
                    if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type, bytes, 16, out required, 0) || type != 13) continue;
                    Guid container = new Guid(bytes);
                    if (instance.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase))
                        foreach (var device in devices)
                            if (instance.StartsWith("BTHENUM\\DEV_" + device.Address.Replace(":", ""), StringComparison.OrdinalIgnoreCase)) device.ContainerId = container;

                    uint levelType, reportedType, chargingType;
                    byte[] level = PropertyBytes(set, ref data, new PropertyKey("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2), out levelType);
                    if (level == null) continue;
                    byte[] reported = PropertyBytes(set, ref data, new PropertyKey("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 7), out reportedType);
                    byte[] charging = PropertyBytes(set, ref data, new PropertyKey("49cd1f76-5626-4b17-a4e8-18b4aa1a2213", 11), out chargingType);
                    var reading = BatteryReading.Parse(level, levelType, reported, reportedType, charging, chargingType, instance);
                    batteryNodes.Add(Tuple.Create(container, reading));
                }
                foreach (var node in batteryNodes) BatteryReading.Attach(devices, node.Item1, node.Item2);
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        static byte[] PropertyBytes(IntPtr set, ref DevInfo info, PropertyKey key, out uint type)
        {
            byte[] buffer = new byte[16]; uint required;
            if (!SetupDiGetDeviceProperty(set, ref info, ref key, out type, buffer, (uint)buffer.Length, out required, 0)) return null;
            if (required > buffer.Length) return null;
            Array.Resize(ref buffer, (int)required); return buffer;
        }

        [StructLayout(LayoutKind.Sequential)] struct SearchParams
        {
            public int Size;
            [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
            [MarshalAs(UnmanagedType.Bool)] public bool Unknown;
            [MarshalAs(UnmanagedType.Bool)] public bool Connected;
            [MarshalAs(UnmanagedType.Bool)] public bool Inquiry;
            public byte Timeout;
            public IntPtr Radio;
        }
        [StructLayout(LayoutKind.Sequential)] struct SystemTime
        { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DeviceInfo
        {
            public int Size;
            public ulong Address;
            public uint Class;
            [MarshalAs(UnmanagedType.Bool)] public bool Connected;
            [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
            [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
            public SystemTime LastSeen, LastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential)] struct DevInfo
        { public int Size; public Guid Class; public uint Instance; public IntPtr Reserved; }
        [DllImport("bthprops.cpl", SetLastError = true)] static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);
        [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BluetoothFindNextDevice(IntPtr handle, ref DeviceInfo info);
        [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BluetoothFindDeviceClose(IntPtr handle);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfo info, StringBuilder id, int size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
