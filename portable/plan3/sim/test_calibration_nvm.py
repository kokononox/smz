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
assert store.load(nvm,'rev-b') is None
assert bytes(nvm[:1536]) == b'\0'*1536
assert bytes(nvm[-16:]) == b'\0'*16
bundle={'calibration':{'profiles':{'game':{}}},'manifest':{'profiles':[{'id':'game'}]},
        'states':[{'id':'game','lo':0,'hi':0}],'stable_ms':0}
store.apply(bundle,profiles)
assert bundle['states'][0]['lo']==22 and bundle['states'][0]['hi']==27
# CAL1 is accepted only for the exact profile revision; edited package wins.
legacy=bytearray(4096)
legacy_payload=__import__('json').dumps({'base':'old-revision','profiles':profiles},separators=(',',':')).encode('utf-8')
legacy[store.BASE:store.BASE+4]=store.LEGACY_MAGIC
legacy[store.BASE+4]=len(legacy_payload)&0xFF
legacy[store.BASE+5]=(len(legacy_payload)>>8)&0xFF
legacy_sum=store._sum16(legacy_payload)
legacy[store.BASE+6]=legacy_sum&0xFF
legacy[store.BASE+7]=(legacy_sum>>8)&0xFF
legacy[store.BASE+store.HEADER:store.BASE+store.HEADER+len(legacy_payload)]=legacy_payload
assert store.load(legacy,'old-revision') == profiles
assert store.load(legacy,'new-revision') is None
# Build 98 CAL2 snapshots had no base_revision and must not override a newly
# edited profile package forever.
old_cal2=bytearray(4096)
old_payload=__import__('json').dumps({'schema':1,'profiles':profiles},separators=(',',':')).encode('utf-8')
old_cal2[store.BASE:store.BASE+4]=store.MAGIC
old_cal2[store.BASE+4]=len(old_payload)&0xFF
old_cal2[store.BASE+5]=(len(old_payload)>>8)&0xFF
old_sum=store._sum16(old_payload)
old_cal2[store.BASE+6]=old_sum&0xFF
old_cal2[store.BASE+7]=(old_sum>>8)&0xFF
old_cal2[store.BASE+store.HEADER:store.BASE+store.HEADER+len(old_payload)]=old_payload
assert store.load(old_cal2,'current-revision') is None
nvm[store.BASE+8] ^= 1
assert store.load(nvm,'rev-a') is None
print('calibration NVM: checksum, revision authority, CAL1 exact migration and reserved regions passed')
