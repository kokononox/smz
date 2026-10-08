from pathlib import Path
root=Path(__file__).resolve().parent
header='/* Generated from read-only local sources. */\n'
for symbol,name in [('embedded_script','Collect-WindowsIdentity.ps1'),('embedded_cs','RegistryNames.cs')]:
 data=(root/name).read_bytes()
 header+='static const unsigned char '+symbol+'[]={\n'+','.join(map(str,data))+'\n};\n'
(root/'embedded_sources.h').write_text(header,encoding='utf-8')
