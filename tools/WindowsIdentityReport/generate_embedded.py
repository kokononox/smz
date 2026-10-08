from pathlib import Path
root=Path(__file__).resolve().parent
header='/* Generated from read-only local sources. */\n'
for symbol,name in [('embedded_script','Collect-WindowsIdentity.ps1'),('embedded_cs','RegistryNames.cs'),('embedded_editor','BootNameEditor.ps1')]:
 data=(root/name).read_text(encoding='utf-8-sig').encode('utf-8-sig')
 header+='static const unsigned char '+symbol+'[]={\n'+','.join(map(str,data))+'\n};\n'
(root/'embedded_sources.h').write_text(header,encoding='utf-8')
