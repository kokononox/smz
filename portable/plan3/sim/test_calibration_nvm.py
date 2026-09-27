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
nvm[store.BASE+8] ^= 1
assert store.load(nvm,'rev-a') is None
print('calibration NVM: checksum, revision binding and reserved regions passed')
