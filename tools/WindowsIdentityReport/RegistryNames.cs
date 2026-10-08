using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Metadata-only registry reader. Never reads SAM user F/V values or password data.
public sealed class ReadOnlyHive : IDisposable
{
    private FileStream stream;
    private uint root;
    public bool Clean { get; private set; }
    private const uint Missing=0xffffffffu;
    public ReadOnlyHive(string path) {
        stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        try {
            byte[] h=Read(0,4096);
            if(Encoding.ASCII.GetString(h,0,4)!="regf")throw new InvalidDataException("Not a Windows registry hive.");
            Clean=U32(h,4)==U32(h,8);
            if(!Clean)throw new InvalidDataException("Hive has pending recovery; read it from the running Windows instead.");
            uint crc=0;for(int i=0;i<508;i+=4)crc^=U32(h,i);
            if(crc==0)crc=1;else if(crc==0xffffffffu)crc=0xfffffffeu;
            if(crc!=U32(h,508))throw new InvalidDataException("Registry header checksum mismatch.");
            root=U32(h,36);Cell(root);
        }catch {stream.Dispose();throw;}
    }
    private byte[] Read(long position,int count) {
        if(position<0||count<0||count>1048576||position>stream.Length-count)throw new InvalidDataException("Invalid registry bounds.");
        byte[] b=new byte[count];stream.Position=position;int n=0;
        while(n<count){int got=stream.Read(b,n,count-n);if(got==0)throw new EndOfStreamException();n+=got;}
        return b;
    }
    private byte[] Cell(uint offset) {
        if(offset==Missing||offset%8!=0)throw new InvalidDataException("Invalid cell reference.");
        long pos=4096L+offset;int size=BitConverter.ToInt32(Read(pos,4),0);
        if(size>=0||size==Int32.MinValue||-size<8||-size%8!=0)throw new InvalidDataException("Cell is not allocated.");
        return Read(pos+4,-size-4);
    }
    private static uint U32(byte[] b,int pos) {if(pos<0||pos>b.Length-4)throw new InvalidDataException("Truncated cell.");return BitConverter.ToUInt32(b,pos);}
    private static ushort U16(byte[] b,int pos) {if(pos<0||pos>b.Length-2)throw new InvalidDataException("Truncated cell.");return BitConverter.ToUInt16(b,pos);}
    private static void Signature(byte[] b,string sig) {if(b.Length<sig.Length||Encoding.ASCII.GetString(b,0,sig.Length)!=sig)throw new InvalidDataException("Unexpected cell type.");}
    private static string NameText(byte[] bytes,int offset,int length,bool compressed) {
        if(!compressed)return Encoding.Unicode.GetString(bytes,offset,length);
        char[] chars=new char[length];for(int i=0;i<length;i++)chars[i]=(char)bytes[offset+i];return new string(chars);
    }
    private string KeyName(uint key) {
        byte[] b=Cell(key);Signature(b,"nk");int n=U16(b,72);
        if(n>b.Length-76||n>32768)throw new InvalidDataException("Invalid name length.");
        bool compressed=(U16(b,2)&32)!=0;
        if(!compressed&&n%2!=0)throw new InvalidDataException("Invalid Unicode name.");
        return NameText(b,76,n,compressed);
    }
    private void Index(uint offset,List<uint> keys,HashSet<uint> seen,int depth) {
        if(depth>16||!seen.Add(offset)||keys.Count>65536)throw new InvalidDataException("Cyclic or excessive subkey index.");
        byte[] b=Cell(offset);if(b.Length<4)throw new InvalidDataException("Truncated index.");
        string kind=Encoding.ASCII.GetString(b,0,2);int count=U16(b,2);
        int width=kind=="lf"||kind=="lh"?8:4;
        if(kind!="lf"&&kind!="lh"&&kind!="li"&&kind!="ri")throw new InvalidDataException("Unsupported index.");
        if(count>(b.Length-4)/width)throw new InvalidDataException("Truncated index entries.");
        for(int i=0;i<count;i++) {
            uint child=U32(b,4+i*width);
            if(kind=="ri")Index(child,keys,seen,depth+1);else keys.Add(child);
        }
    }
    private List<uint> Children(uint key) {
        byte[] b=Cell(key);Signature(b,"nk");uint count=U32(b,20);var result=new List<uint>();
        if(count==0)return result;if(count>65536)throw new InvalidDataException("Excessive subkey count.");
        Index(U32(b,28),result,new HashSet<uint>(),0);
        if(result.Count!=count)throw new InvalidDataException("Subkey count mismatch.");return result;
    }
    private uint Find(string path) {
        uint at=root;
        foreach(string part in path.Split(new char[]{'\\'},StringSplitOptions.RemoveEmptyEntries)) {
            uint next=Missing;
            foreach(uint child in Children(at))if(String.Equals(KeyName(child),part,StringComparison.OrdinalIgnoreCase)){next=child;break;}
            if(next==Missing)throw new InvalidDataException("Registry key not found: "+path);at=next;
        }
        return at;
    }
    public string[] Names(string path) {
        List<string> result=new List<string>();foreach(uint child in Children(Find(path)))result.Add(KeyName(child));
        result.Sort(StringComparer.OrdinalIgnoreCase);return result.ToArray();
    }
    private byte[] Value(string path,string name,out uint type) {
        byte[] nk=Cell(Find(path));uint count=U32(nk,36);type=0;
        if(count>65536)throw new InvalidDataException("Excessive value count.");
        if(count==0)return null;byte[] list=Cell(U32(nk,40));
        if(count>list.Length/4)throw new InvalidDataException("Truncated value list.");
        for(int i=0;i<count;i++) {
            byte[] v=Cell(U32(list,i*4));Signature(v,"vk");int nameSize=U16(v,2);
            if(nameSize>v.Length-20)throw new InvalidDataException("Truncated value name.");
            bool compressed=(U16(v,16)&1)!=0;
            if(!String.Equals(NameText(v,20,nameSize,compressed),name,StringComparison.OrdinalIgnoreCase))continue;
            uint raw=U32(v,4),length=raw&0x7fffffffu;type=U32(v,12);
            if(length>65536)throw new InvalidDataException("Value is too large for metadata.");
            byte[] data=new byte[length];
            if((raw&0x80000000u)!=0){if(length>4)throw new InvalidDataException("Invalid inline value.");Array.Copy(v,8,data,0,(int)length);}
            else {byte[] cell=Cell(U32(v,8));if(length>cell.Length)throw new InvalidDataException("Truncated value.");Array.Copy(cell,data,(int)length);}
            return data;
        }
        return null;
    }
    public string StringValue(string path,string name) {
        uint type;byte[] data=Value(path,name,out type);if(data==null)return null;
        if((type!=1&&type!=2)||data.Length%2!=0)throw new InvalidDataException("Not a string metadata value.");
        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }
    public uint DwordValue(string path,string name) {
        uint type;byte[] data=Value(path,name,out type);if(data==null||type!=4||data.Length!=4)throw new InvalidDataException("Not a DWORD metadata value.");
        return BitConverter.ToUInt32(data,0);
    }
    public void Dispose(){if(stream!=null)stream.Dispose();}
}

public static class NativeVolume {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint QueryDosDevice(string device,StringBuilder buffer,int count);
    public static string DevicePath(string drive) {
        StringBuilder b=new StringBuilder(32768);if(QueryDosDevice(drive,b,b.Capacity)==0)return null;
        return b.ToString().Split('\0')[0];
    }
}
