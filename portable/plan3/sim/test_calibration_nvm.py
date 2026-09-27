#!/usr/bin/env python3
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/'portable/plan3/CIRCUITPY-MODERN'))
import calibration_nvm as store
nvm=bytearray(4096)
profiles={'game':{'center':24.2,'tolerance':2.0,'stable_ms':750}}
store.save(nvm,'rev-a',profiles)
assert store.load(nvm,'rev-a') == profiles
assert store.load(nvm,'rev-b') == profiles
assert bytes(nvm[:1536]) == b'\0'*1536
assert bytes(nvm[-16:]) == b'\0'*16
bundle={'calibration':{'profiles':{'game':{}}},'manifest':{'profiles':[{'id':'game'}]},
        'states':[{'id':'game','lo':0,'hi':0}],'stable_ms':0}
store.apply(bundle,profiles)
assert bundle['states'][0]['lo']==22 and bundle['states'][0]['hi']==27
# CAL1 snapshots from Build 104 migrate across normal project exports.
legacy=bytearray(4096)
legacy_payload=__import__('json').dumps({'base':'old-revision','profiles':profiles},separators=(',',':')).encode('utf-8')
legacy[store.BASE:store.BASE+4]=store.LEGACY_MAGIC
legacy[store.BASE+4]=len(legacy_payload)&0xFF
legacy[store.BASE+5]=(len(legacy_payload)>>8)&0xFF
legacy_sum=store._sum16(legacy_payload)
legacy[store.BASE+6]=legacy_sum&0xFF
legacy[store.BASE+7]=(legacy_sum>>8)&0xFF
legacy[store.BASE+store.HEADER:store.BASE+store.HEADER+len(legacy_payload)]=legacy_payload
assert store.load(legacy,'new-revision') == profiles
nvm[store.BASE+8] ^= 1
assert store.load(nvm,'rev-a') is None
print('calibration NVM: checksum, revision independence, CAL1 migration and reserved regions passed')
