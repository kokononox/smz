using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Read-only inventory of local Plug and Play instances. Removal is delegated to
// the signed inbox PnPUtil, without /subtree /force /reboot or driver deletion.
public sealed class DeviceInventoryRow {
    public string Id,Name,Class,ClassGuid,Service,Manufacturer,Location,Enumerator,DriverKey,Parent;
    public string[] HardwareIds;
    public bool Present,ParentKnown;
}
public static class LocalDeviceInventory {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA {public uint cbSize;public Guid ClassGuid;public uint DevInst;public UIntPtr Reserved;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid,string enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref SP_DEVINFO_DATA info,StringBuilder id,uint size,out uint required);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set,ref SP_DEVINFO_DATA info,uint property,out uint type,byte[] data,uint size,out uint required);
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY {public Guid fmtid;public uint pid;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceProperty(IntPtr set,ref SP_DEVINFO_DATA info,ref DEVPROPKEY key,out uint type,byte[] data,uint size,out uint required,uint flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    static string RegisteredParent(IntPtr set,ref SP_DEVINFO_DATA info) {
        DEVPROPKEY key=new DEVPROPKEY();key.fmtid=new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7");key.pid=8;
        uint type,needed;byte[] data=new byte[8192];
        if(!SetupDiGetDeviceProperty(set,ref info,ref key,out type,data,(uint)data.Length,out needed,0))return null;
        if(type!=18||needed<2||needed>data.Length||needed%2!=0)return null;
        return Encoding.Unicode.GetString(data,0,(int)needed).TrimEnd('\0');
    }
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent,uint child,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint devInst,StringBuilder id,uint size,uint flags);
    static IntPtr Open(bool present) {
        IntPtr set=SetupDiGetClassDevs(IntPtr.Zero,null,IntPtr.Zero,present?6u:4u);
        if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());return set;
    }
    static SP_DEVINFO_DATA Info(){SP_DEVINFO_DATA d=new SP_DEVINFO_DATA();d.cbSize=(uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));return d;}
    static string Id(IntPtr set,ref SP_DEVINFO_DATA d) {
        StringBuilder text=new StringBuilder(4096);uint needed;
        if(!SetupDiGetDeviceInstanceId(set,ref d,text,(uint)text.Capacity,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());return text.ToString();
    }
    static string[] Property(IntPtr set,ref SP_DEVINFO_DATA d,uint prop) {
        uint type,needed;byte[] data=new byte[16384];
        if(!SetupDiGetDeviceRegistryProperty(set,ref d,prop,out type,data,(uint)data.Length,out needed)){
            int e=Marshal.GetLastWin32Error();if(e==13||e==2||e==5)return new string[0];
            if(e!=122||needed>1048576)throw new Win32Exception(e);
            data=new byte[needed];if(!SetupDiGetDeviceRegistryProperty(set,ref d,prop,out type,data,(uint)data.Length,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if(type!=1&&type!=7)return new string[0];
        if(needed%2!=0)throw new InvalidOperationException("Malformed device property.");
        return Encoding.Unicode.GetString(data,0,(int)needed).Split(new char[]{'\0'},StringSplitOptions.RemoveEmptyEntries);
    }
    static string Text(IntPtr set,ref SP_DEVINFO_DATA d,uint prop){string[] s=Property(set,ref d,prop);return s.Length==0?"":s[0];}
    public static string[] PresentIds() {
        List<string> result=new List<string>();IntPtr set=Open(true);
        try{for(uint i=0;i<100000;i++){SP_DEVINFO_DATA d=Info();if(!SetupDiEnumDeviceInfo(set,i,ref d)){int e=Marshal.GetLastWin32Error();if(e==259)break;throw new Win32Exception(e);}result.Add(Id(set,ref d));}if(result.Count>=100000)throw new InvalidOperationException("Device inventory limit exceeded; no partial scan permitted.");}
        finally{SetupDiDestroyDeviceInfoList(set);}return result.ToArray();
    }
    public static DeviceInventoryRow[] Snapshot() {
        HashSet<string> present=new HashSet<string>(PresentIds(),StringComparer.OrdinalIgnoreCase);
        List<DeviceInventoryRow> result=new List<DeviceInventoryRow>();IntPtr set=Open(false);
        try{
            for(uint i=0;i<100000;i++){
                SP_DEVINFO_DATA d=Info();if(!SetupDiEnumDeviceInfo(set,i,ref d)){int e=Marshal.GetLastWin32Error();if(e==259)break;throw new Win32Exception(e);}
                DeviceInventoryRow row=new DeviceInventoryRow();row.Id=Id(set,ref d);row.Present=present.Contains(row.Id);
                row.ClassGuid=d.ClassGuid.ToString("B");row.Class=Text(set,ref d,7);row.Name=Text(set,ref d,12);if(row.Name.Length==0)row.Name=Text(set,ref d,0);if(row.Name.Length==0)row.Name=row.Id;
                row.Service=Text(set,ref d,4);row.Manufacturer=Text(set,ref d,11);row.Location=Text(set,ref d,13);row.Enumerator=Text(set,ref d,22);row.DriverKey=Text(set,ref d,9);row.HardwareIds=Property(set,ref d,1);
                uint parent;uint status=CM_Get_Parent(out parent,d.DevInst,0);
                if(status==0){StringBuilder text=new StringBuilder(4096);if(CM_Get_Device_ID(parent,text,(uint)text.Capacity,0)==0){row.Parent=text.ToString();row.ParentKnown=true;}}
                // Persisted DEVPKEY_Device_Parent also covers non-present devnodes where CM ancestry is unavailable.
                if(!row.ParentKnown){string saved=RegisteredParent(set,ref d);if(!String.IsNullOrEmpty(saved)){row.Parent=saved;row.ParentKnown=true;}}
                // No guessed ancestry. Missing information will be guarded by policy.
                result.Add(row);
            }
        if(result.Count>=100000)throw new InvalidOperationException("Device inventory limit exceeded; no partial scan permitted.");
        }finally{SetupDiDestroyDeviceInfoList(set);}
        return result.ToArray();
    }
}
