using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace HotspotReconnect
{
    public sealed class Settings
    {
        public string Profile = "";
        public string Adapter = "";
        public bool Watch = false;
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotspotReconnect"); } }
        public static Settings Load()
        {
            try { using (var f = File.OpenRead(Path.Combine(Folder, "settings.xml"))) return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(f); }
            catch { return new Settings(); }
        }
        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, "settings.xml");
            string temp = path + ".tmp";
            using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, this);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }

    public sealed class Adapter
    {
        public Guid Id;
        public string Name;
        public int State;
    }
    public sealed class Profile
    {
        public Guid AdapterId;
        public string Name;
        public string AdapterName;
        public override string ToString() { return Name + "  —  " + AdapterName; }
    }
    public interface IWifi : IDisposable
    {
        List<Adapter> Adapters();
        List<Profile> Profiles(Adapter adapter);
        void Connect(Guid adapter, string profile);
    }

    public sealed class NativeWifi : IWifi
    {
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct InterfaceInfo
        {
            public Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProfileInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ConnectionParameters
        {
            public int Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid;
            public IntPtr Bssid;
            public int BssType;
            public uint Flags;
        }
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanGetProfileList(IntPtr handle, ref Guid adapter, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr handle, ref Guid adapter, ref ConnectionParameters parameters, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);

        static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
        public NativeWifi() { uint version; Check(WlanOpenHandle(2, IntPtr.Zero, out version, out handle)); }
        public List<Adapter> Adapters()
        {
            IntPtr list; Check(WlanEnumInterfaces(handle, IntPtr.Zero, out list));
            try
            {
                var result = new List<Adapter>();
                int count = Marshal.ReadInt32(list), size = Marshal.SizeOf(typeof(InterfaceInfo));
                for (int i = 0; i < count; i++)
                {
                    var item = (InterfaceInfo)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * size), typeof(InterfaceInfo));
                    result.Add(new Adapter { Id = item.Id, Name = item.Description, State = item.State });
                }
                return result;
            }
            finally { WlanFreeMemory(list); }
        }
        public List<Profile> Profiles(Adapter adapter)
        {
            IntPtr list; Guid id = adapter.Id; Check(WlanGetProfileList(handle, ref id, IntPtr.Zero, out list));
            try
            {
                var result = new List<Profile>();
                int count = Marshal.ReadInt32(list), size = Marshal.SizeOf(typeof(ProfileInfo));
                for (int i = 0; i < count; i++)
                {
                    var item = (ProfileInfo)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * size), typeof(ProfileInfo));
                    result.Add(new Profile { AdapterId = id, Name = item.Name, AdapterName = adapter.Name });
                }
                return result;
            }
            finally { WlanFreeMemory(list); }
        }
        public void Connect(Guid adapter, string profile)
        {
            var p = new ConnectionParameters { Mode = 0, Profile = profile, BssType = 1 };
            Check(WlanConnect(handle, ref adapter, ref p, IntPtr.Zero));
        }
        public void Dispose() { if (handle != IntPtr.Zero) { WlanCloseHandle(handle, IntPtr.Zero); handle = IntPtr.Zero; } }
        public static void CheckLayout()
        {
            if (Marshal.SizeOf(typeof(InterfaceInfo)) != 532 || Marshal.SizeOf(typeof(ProfileInfo)) != 516)
                throw new Exception("Unexpected WLAN structure layout");
            int expected = IntPtr.Size == 8 ? 40 : 24;
            if (Marshal.SizeOf(typeof(ConnectionParameters)) != expected) throw new Exception("Unexpected connection parameters layout");
        }
    }

    public sealed class Watcher
    {
        public string CheckOnce(IWifi wifi, Profile target, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var adapters = wifi.Adapters();
            Adapter selected = adapters.Find(delegate(Adapter a) { return a.Id == target.AdapterId; });
            if (selected == null) return "未找到所选无线网卡，请检查网卡或重新选择热点。";
            if (selected.State == 1) return "本次检查：所选网卡已连接 Wi-Fi，无需重连。";
            if (selected.State == 5 || selected.State == 6 || selected.State == 7)
                return "本次检查：Windows 正在连接或验证，交由系统继续处理。";
            if (selected.State == 0) return "无线网卡尚未就绪，请确认 Wi-Fi 已开启、飞行模式已关闭。";
            if (selected.State != 4) return "无线网卡当前不可重连；恢复后可点击“立即检查”。";
            cancel.ThrowIfCancellationRequested();
            wifi.Connect(target.AdapterId, target.Name);
            return "已请求连接「" + target.Name + "」一次，请在 Windows 中查看结果。";
        }
    }

}
