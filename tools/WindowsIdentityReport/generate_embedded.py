from pathlib import Path
root=Path(__file__).resolve().parent
header='/* Generated from local report/editor sources. */\n'
for symbol,name in [('embedded_script','Collect-WindowsIdentity.ps1'),('embedded_cs','RegistryNames.cs'),('embedded_editor','BootNameEditor.ps1'),('embedded_identity','IdentityEditor.ps1'),('embedded_guids','BootGuidEditor.ps1'),('embedded_stager','BcdGuidStager.cs'),('embedded_devices','DeviceCleanup.ps1'),('embedded_device_api','DeviceCleanup.cs')]:
 data=(root/name).read_text(encoding='utf-8-sig').encode('utf-8-sig')
 header+='static const unsigned char '+symbol+'[]={\n'+','.join(map(str,data))+'\n};\n'
(root/'embedded_sources.h').write_text(header,encoding='utf-8')
