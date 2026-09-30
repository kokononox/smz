import hashlib,struct,unittest
from tools.abvm_slot import DEFAULT_CAPACITY,SLOT_HEADER_SIZE,SLOT_MAGIC,SLOT_VERSION,render
from tools.abvm_uf2 import UF2_MAGIC0,UF2_MAGIC1,UF2_MAGIC_END,Uf2Error,inject,locate_slot,parse

def make_uf2(program:bytes,capacity:int=1024,base:int=0x10020000)->bytes:
    digest=hashlib.sha256(program).digest(); slot=SLOT_MAGIC+struct.pack("<IIII",SLOT_VERSION,SLOT_HEADER_SIZE,capacity,len(program))+digest+program+bytes(capacity-len(program)); flash=bytes(256)+slot; total=(len(flash)+255)//256; flash+=bytes(total*256-len(flash)); blocks=[]
    for number in range(total):
        payload=flash[number*256:(number+1)*256]; raw=bytearray(512); struct.pack_into("<IIIIIIII",raw,0,UF2_MAGIC0,UF2_MAGIC1,0x2000,base+number*256,256,number,total,0xE48BFF56); raw[32:288]=payload; struct.pack_into("<I",raw,508,UF2_MAGIC_END); blocks.append(raw)
    return b"".join(blocks)
class AbvmUf2Tests(unittest.TestCase):
    def test_slot_c_source_has_fixed_layout(self):
        source=render(b"ABP1test",1024); self.assertIn('section(".abvm_slot")',source); self.assertIn("ABVM_FLASH_SLOT_CAPACITY 1024u",source); self.assertIn("offsetof(AbvmFlashSlot, program) == 64u",source); self.assertIn("const volatile uint32_t *program_size",source); self.assertIn("__attribute__((noinline))",source); self.assertNotIn("return abvm_flash_slot.program_size",source)
    def test_default_slot_has_256_kib_capacity(self):
        self.assertEqual(DEFAULT_CAPACITY,256*1024)
        self.assertIn("ABVM_FLASH_SLOT_CAPACITY 262144u",render(b"ABP1test"))
    def test_inject_replaces_program_and_preserves_shape(self):
        template=make_uf2(b"ABP1old"); program=b"ABP1"+bytes(range(200)); output,info=inject(template,program); self.assertEqual(len(output),len(template)); self.assertEqual(info["capacity"],1024); self.assertEqual(info["program_size"],len(program)); self.assertEqual(info["program_sha256"],hashlib.sha256(program).hexdigest()); blocks=parse(output); address,_,size=locate_slot(blocks); memory={block.address+i:value for block in blocks for i,value in enumerate(block.payload)}; self.assertEqual(bytes(memory[address+SLOT_HEADER_SIZE+i] for i in range(size)),program)
    def test_rejects_program_over_capacity(self):
        with self.assertRaisesRegex(Uf2Error,"exceeds slot capacity"): inject(make_uf2(b"ABP1old",64),b"ABP1"+bytes(100))
    def test_rejects_invalid_uf2_and_non_abp(self):
        broken=bytearray(make_uf2(b"ABP1old")); broken[0]^=1
        with self.assertRaisesRegex(Uf2Error,"invalid UF2 magic"): inject(bytes(broken),b"ABP1new")
        with self.assertRaisesRegex(Uf2Error,"not an ABP1"): inject(make_uf2(b"ABP1old"),b"NOPE")
    def test_rejects_ambiguous_slot(self):
        blocks=parse(make_uf2(b"ABP1old",1024)); blocks[0].payload[16:16+len(SLOT_MAGIC)]=SLOT_MAGIC; ambiguous=b"".join(bytes(b.raw) for b in blocks)
        with self.assertRaisesRegex(Uf2Error,"exactly one ABVM slot"): inject(ambiguous,b"ABP1new")
if __name__=="__main__": unittest.main()
