#!/usr/bin/env python3
"""Inject an ABP image into the fixed flash slot of a native ABVM UF2."""
from __future__ import annotations
import argparse, hashlib, struct
from dataclasses import dataclass
from pathlib import Path
try:
    from tools.abvm_slot import SLOT_HEADER_SIZE,SLOT_MAGIC,SLOT_VERSION
except ModuleNotFoundError:
    from abvm_slot import SLOT_HEADER_SIZE,SLOT_MAGIC,SLOT_VERSION
UF2_MAGIC0=0x0A324655; UF2_MAGIC1=0x9E5D5157; UF2_MAGIC_END=0x0AB16F30; UF2_BLOCK_SIZE=512; UF2_DATA_MAX=476
_HEADER=struct.Struct("<IIIIIIII"); _SLOT_META=struct.Struct("<IIII")
class Uf2Error(ValueError): pass
@dataclass
class Block:
    raw:bytearray; flags:int; address:int; payload_size:int; number:int; total:int; family:int
    @property
    def payload(self)->memoryview: return memoryview(self.raw)[32:32+self.payload_size]

def parse(data:bytes)->list[Block]:
    if not data or len(data)%UF2_BLOCK_SIZE: raise Uf2Error("UF2 size is not a non-zero multiple of 512")
    blocks=[]; numbers=set(); expected=len(data)//UF2_BLOCK_SIZE
    for offset in range(0,len(data),UF2_BLOCK_SIZE):
        raw=bytearray(data[offset:offset+UF2_BLOCK_SIZE]); m0,m1,flags,addr,size,number,total,family=_HEADER.unpack_from(raw); end,=struct.unpack_from("<I",raw,508)
        if (m0,m1,end)!=(UF2_MAGIC0,UF2_MAGIC1,UF2_MAGIC_END): raise Uf2Error(f"invalid UF2 magic in block {offset//UF2_BLOCK_SIZE}")
        if not 0<size<=UF2_DATA_MAX: raise Uf2Error(f"invalid payload size {size}")
        if total!=expected or number>=total or number in numbers: raise Uf2Error("invalid or duplicate UF2 block numbering")
        numbers.add(number); blocks.append(Block(raw,flags,addr,size,number,total,family))
    if numbers!=set(range(expected)): raise Uf2Error("UF2 block sequence is incomplete")
    return blocks

def _address_map(blocks:list[Block])->dict[int,tuple[Block,int]]:
    result={}
    for block in blocks:
        for i in range(block.payload_size):
            address=block.address+i
            if address in result: raise Uf2Error("overlapping UF2 payload ranges")
            result[address]=(block,i)
    return result

def _read(memory,address,size):
    try: return bytes(memory[address+i][0].payload[memory[address+i][1]] for i in range(size))
    except KeyError as exc: raise Uf2Error("flash slot is not fully materialized in UF2") from exc

def _write(memory,address,data):
    for i,value in enumerate(data):
        try: block,index=memory[address+i]
        except KeyError as exc: raise Uf2Error("flash slot is not fully materialized in UF2") from exc
        block.payload[index]=value

def locate_slot(blocks:list[Block])->tuple[int,int,int]:
    memory=_address_map(blocks); candidates=[]; first=SLOT_MAGIC[0]
    for address,(block,index) in memory.items():
        if block.payload[index]==first:
            try:
                if _read(memory,address,len(SLOT_MAGIC))==SLOT_MAGIC: candidates.append(address)
            except Uf2Error: pass
    if len(candidates)!=1: raise Uf2Error(f"expected exactly one ABVM slot, found {len(candidates)}")
    address=candidates[0]; version,header_size,capacity,program_size=_SLOT_META.unpack(_read(memory,address+16,_SLOT_META.size))
    if version!=SLOT_VERSION or header_size!=SLOT_HEADER_SIZE: raise Uf2Error("unsupported ABVM slot layout")
    if capacity<=0 or program_size>capacity: raise Uf2Error("invalid ABVM slot capacity or program size")
    _read(memory,address,header_size+capacity); return address,capacity,program_size

def inject(template:bytes,program:bytes):
    if program[:4]!=b"ABP1": raise Uf2Error("program is not an ABP1 image")
    blocks=parse(template); memory=_address_map(blocks); address,capacity,_=locate_slot(blocks)
    if len(program)>=128 and struct.unpack_from("<HH",program,4)==(2,1):
        try:
            from tools.abvm import Verifier,OP_BUFF,OP_SHIFT_CHECK,OP_SHIFT_TYPE
        except ModuleNotFoundError:
            from abvm import Verifier,OP_BUFF,OP_SHIFT_CHECK,OP_SHIFT_TYPE
        verified=Verifier.verify(program)
        flash=b"".join(bytes(b.payload) for b in sorted(blocks,key=lambda b:b.address))
        if any(i.op in (OP_SHIFT_CHECK,OP_SHIFT_TYPE) for i in verified.instructions):
            if b"OK|SHIFT-CHALLENGE|" not in flash:
                raise Uf2Error("UF2 template lacks the Pico shift identity runtime; choose a matching Shift test-build template")
        if any(i.op==OP_BUFF for i in verified.instructions):
            flash=b"".join(bytes(b.payload) for b in sorted(blocks,key=lambda b:b.address))
            if b"EVT|BUFF|state=new-game|count=%u" not in flash:
                raise Uf2Error("UF2 template lacks the Pico buff runtime; select a matching GameBuff test-build template")
    if len(program)>capacity: raise Uf2Error(f"program exceeds slot capacity ({len(program)} > {capacity})")
    digest=hashlib.sha256(program).digest(); _write(memory,address+16,_SLOT_META.pack(SLOT_VERSION,SLOT_HEADER_SIZE,capacity,len(program))); _write(memory,address+32,digest); _write(memory,address+SLOT_HEADER_SIZE,program+bytes(capacity-len(program)))
    result=b"".join(bytes(b.raw) for b in sorted(blocks,key=lambda b:b.number)); check=parse(result); cm=_address_map(check); ca,cc,cs=locate_slot(check)
    if (ca,cc,cs)!=(address,capacity,len(program)) or _read(cm,address+SLOT_HEADER_SIZE,len(program))!=program: raise Uf2Error("post-write slot verification failed")
    return result,{"slot_address":address,"capacity":capacity,"program_size":len(program),"program_sha256":digest.hex()}

def main()->int:
    p=argparse.ArgumentParser(); p.add_argument("template",type=Path); p.add_argument("program",type=Path); p.add_argument("output",type=Path); a=p.parse_args()
    try: output,info=inject(a.template.read_bytes(),a.program.read_bytes())
    except Uf2Error as exc: p.error(str(exc))
    a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_bytes(output); print(f"slot=0x{info['slot_address']:08x} capacity={info['capacity']} program={info['program_size']} sha256={info['program_sha256']}"); return 0
if __name__=="__main__": raise SystemExit(main())
