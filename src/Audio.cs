using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SoundcoreDesk
{
    public sealed class AudioDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Guid ContainerId { get; set; }
        public uint State { get; set; }
        public bool IsDefault { get; set; }
        public string BluetoothAddress { get; set; }
        public bool Connected { get { return State == 1; } }
        public bool IsGlow { get { return Name.IndexOf("glow mini", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool HandsFree { get { return Name.IndexOf("hands-free", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public override string ToString() { return Name + (string.IsNullOrEmpty(BluetoothAddress) ? "" : " · " + BluetoothAddress.Substring(12)) + (Connected ? " · 已連線" : " · 未連線"); }
    }

    public sealed class VolumeState
    {
        public float Volume { get; set; }
        public bool Muted { get; set; }
    }

    public static class Audio
    {
        static readonly PropertyKey FriendlyName = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly PropertyKey Container = new PropertyKey("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        public static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
        public static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

        public static List<AudioDevice> Devices()
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
            IMMDeviceCollection collection = null;
            IMMDevice defaultDevice = null;
            var devices = new List<AudioDevice>();
            try
            {
                string defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) == 0) Check(defaultDevice.GetId(out defaultId));
                Check(enumerator.EnumAudioEndpoints(0, 15, out collection));
                uint count; Check(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null; IPropertyStore properties = null;
                    try
                    {
                        Check(collection.Item(i, out device));
                        string id; uint state;
                        Check(device.GetId(out id)); Check(device.GetState(out state));
                        Check(device.OpenPropertyStore(0, out properties));
                        var name = ReadProperty(properties, FriendlyName) as string;
                        var container = ReadProperty(properties, Container);
                        devices.Add(new AudioDevice { Id = id, Name = name ?? "音訊裝置", State = state,
                            ContainerId = container is Guid ? (Guid)container : Guid.Empty, IsDefault = id == defaultId });
                    }
                    catch (COMException) { /* An endpoint can disappear while enumerating. */ }
                    finally { Release(properties); Release(device); }
                }
                return devices;
            }
            finally { Release(defaultDevice); Release(collection); Release(enumerator); }
        }

        static object ReadProperty(IPropertyStore store, PropertyKey key)
        {
            PropVariant value;
            if (store.GetValue(ref key, out value) < 0) return null;
            try
            {
                if (value.Type == 31) return Marshal.PtrToStringUni(value.Pointer);
                if (value.Type == 72 && value.Pointer != IntPtr.Zero) return (Guid)Marshal.PtrToStructure(value.Pointer, typeof(Guid));
                return null;
            }
            finally { PropVariantClear(ref value); }
        }

        static T WithVolume<T>(string id, Func<IAudioEndpointVolume, T> action)
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
            IMMDevice device = null; object endpoint = null;
            try
            {
                Check(enumerator.GetDevice(id, out device));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out endpoint));
                return action((IAudioEndpointVolume)endpoint);
            }
            finally { Release(endpoint); Release(device); Release(enumerator); }
        }
        public static VolumeState Read(string id)
        {
            return WithVolume(id, delegate(IAudioEndpointVolume endpoint) {
                float volume; bool mute;
                Check(endpoint.GetMasterVolumeLevelScalar(out volume)); Check(endpoint.GetMute(out mute));
                return new VolumeState { Volume = volume, Muted = mute };
            });
        }
        public static void SetVolume(string id, float volume)
        {
            WithVolume(id, delegate(IAudioEndpointVolume endpoint) {
                Guid context = Guid.Empty;
                Check(endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, volume)), ref context)); return 0;
            });
        }
        public static void SetMute(string id, bool muted)
        {
            WithVolume(id, delegate(IAudioEndpointVolume endpoint) {
                Guid context = Guid.Empty; Check(endpoint.SetMute(muted, ref context)); return 0;
            });
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey
    {
        public Guid Format; public uint Id;
        public PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
}
