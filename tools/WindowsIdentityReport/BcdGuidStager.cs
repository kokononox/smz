using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// App-hive operations are restricted to exported disposable copies. Never pass the live BCD path.
public static class BcdGuidStager {
    const int Read=0x20019, All=0xf003f;
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegLoadAppKey(string file,out IntPtr key,int access,int options,int reserved);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegOpenKeyEx(IntPtr key,string path,int options,int access,out IntPtr result);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegCreateKeyEx(IntPtr key,string path,int reserved,string cls,int options,int access,IntPtr security,out IntPtr result,out int disposition);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegCopyTree(IntPtr source,string subkey,IntPtr destination);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegQueryValueEx(IntPtr key,string name,IntPtr reserved,out uint type,byte[] data,ref uint size);
    [DllImport("advapi32.dll")] static extern int RegFlushKey(IntPtr key);
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
    static void Check(int result) {if(result!=0)throw new Win32Exception(result);}
    static string GuidKey(string id) {Guid g;if(!Guid.TryParseExact(id,"B",out g)||g==Guid.Empty)throw new ArgumentException("Invalid BCD GUID.");return g.ToString("B");}
    static void CheckFile(string path) {
        path=Path.GetFullPath(path);
        // Only exported/staged .bcd files are accepted, not a system store named BCD.
        if(!String.Equals(Path.GetExtension(path),".bcd",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("An exported .bcd copy is required.");
        if(!File.Exists(path))throw new FileNotFoundException("Exported BCD copy not found.");
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Reparse-point BCD copies are rejected.");
    }
    public static void CloneObject(string path,string oldId,string newId) {
        CheckFile(path);oldId=GuidKey(oldId);newId=GuidKey(newId);if(oldId==newId)throw new ArgumentException("Identifiers must differ.");
        IntPtr hive=IntPtr.Zero,source=IntPtr.Zero,dest=IntPtr.Zero;
        try{
            Check(RegLoadAppKey(path,out hive,All,1,0));Check(RegOpenKeyEx(hive,"Objects\\"+oldId,0,Read,out source));
            int disposition;Check(RegCreateKeyEx(hive,"Objects\\"+newId,0,null,0,All,IntPtr.Zero,out dest,out disposition));
            if(disposition!=1)throw new InvalidOperationException("The new GUID already exists; refusing overwrite.");
            Check(RegCopyTree(source,null,dest));Check(RegFlushKey(hive));
        }finally{if(dest!=IntPtr.Zero)RegCloseKey(dest);if(source!=IntPtr.Zero)RegCloseKey(source);if(hive!=IntPtr.Zero)RegCloseKey(hive);}
    }
    static void HashTree(RegistryKey key,string relative,BinaryWriter writer) {
        // A description rename is allowed between creation and cleanup; all other bytes must match.
        if(String.Equals(relative,"Elements\\12000004",StringComparison.OrdinalIgnoreCase))return;
        writer.Write(relative.ToLowerInvariant());
        string[] values=key.GetValueNames();Array.Sort(values,StringComparer.OrdinalIgnoreCase);
        foreach(string name in values){
            uint type,size=0;IntPtr handle=key.Handle.DangerousGetHandle();
            Check(RegQueryValueEx(handle,name,IntPtr.Zero,out type,null,ref size));if(size>16*1024*1024)throw new InvalidDataException("Excessive BCD value size.");
            byte[] bytes=new byte[size];Check(RegQueryValueEx(handle,name,IntPtr.Zero,out type,bytes,ref size));
            writer.Write(name.ToLowerInvariant());writer.Write(type);writer.Write(size);writer.Write(bytes,0,(int)size);
        }
        string[] children=key.GetSubKeyNames();Array.Sort(children,StringComparer.OrdinalIgnoreCase);
        foreach(string child in children)using(RegistryKey sub=key.OpenSubKey(child,false))HashTree(sub,relative.Length==0?child:relative+"\\"+child,writer);
    }
    public static string Fingerprint(string path,string id) {
        CheckFile(path);id=GuidKey(id);IntPtr hive=IntPtr.Zero;
        try{
            Check(RegLoadAppKey(path,out hive,Read,1,0));
            using(SafeRegistryHandle handle=new SafeRegistryHandle(hive,false))
            using(RegistryKey root=RegistryKey.FromHandle(handle))
            using(RegistryKey obj=root.OpenSubKey("Objects\\"+id,false)){
                if(obj==null)throw new InvalidDataException("BCD object not found in exported copy.");
                using(MemoryStream mem=new MemoryStream()){
                    using(BinaryWriter writer=new BinaryWriter(mem,Encoding.UTF8,true)){HashTree(obj,"",writer);writer.Flush();}
                    using(SHA256 sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(mem.ToArray())).Replace("-","").ToLowerInvariant();
                }
            }
        }finally{if(hive!=IntPtr.Zero)RegCloseKey(hive);}
    }
}
