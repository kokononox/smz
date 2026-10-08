import copy,hashlib,json,pathlib,subprocess,sys,tempfile,unittest
ROOT=pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/'tools'));import abvm
ROUTES=('Desktop','Restart','Startup','LoginOrDc','Dc','CharacterDashboard','EnteringGameLoading','Game','Targeted','TargetedRepeat','Whisper','WhisperRepeat','Finish','SwitchToDay','SwitchToNight')
def source():
 d=json.loads((ROOT/'firmware/abvm/tests/abvm_guard_smoke.amsj').read_text())
 d['shiftSchedule']={'enabled':True,'dayStart':480,'dayEnd':1080,'nightStart':1200,'nightEnd':360,'maxAttempts':2}
 d['nativeShift']={'dayHash':hashlib.sha256(b'DAYUSER').hexdigest(),'nightHash':hashlib.sha256(b'NIGHTUSER').hexdigest()}
 for tab in ('Desktop','Startup'):
  d['pipelines'][tab].insert(0,{'Type':'shiftCheck','Props':{'key':'4','modWin':True,'holdMin':90,'holdMax':160},'Children':[]})
 for tab,key in [('SwitchToDay','5'),('SwitchToNight','6')]:d['pipelines'][tab]=[{'Type':'keystroke','Props':{'key':key,'modWin':True,'holdMin':90,'holdMax':160},'Children':[]}]
 return d
class ScheduleTests(unittest.TestCase):
 def build(self,d,c,extra=(),include=()):
  exe=d/'test';subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',*['-I'+str(p) for p in include],'-I'+str(ROOT/'firmware/abvm/include'),'-I'+str(ROOT/'firmware/abvm/pico'),str(c),*map(str,extra),'-o',str(exe)],check=True);return exe
 def test_all_minutes_actual_core(self):
  with tempfile.TemporaryDirectory() as t:
   d=pathlib.Path(t);exe=self.build(d,ROOT/'firmware/abvm/tests/shift_schedule_runtime_smoke.c',[ROOT/'firmware/abvm/pico/shift_identity_runtime.c']);subprocess.run([str(exe)],check=True)
 def test_compiler_and_verifier(self):
  p=abvm.Compiler().compile_amsj(source(),ROUTES);v=abvm.Verifier.verify(p.image)
  desc=[v.const(i.a,abvm.CONST_SHIFT) for i in v.instructions if i.op==abvm.OP_SHIFT_CHECK]
  self.assertEqual(len(desc),2);self.assertTrue(all(x[:4]==b'SFT2' and len(x)==104 for x in desc))
  self.assertTrue({16,17}.issubset({r.route_id for r in v.routes}))
 def test_reject_overlap_empty_clock_and_budget(self):
  for update in [{'nightStart':1000},{'dayStart':480,'dayEnd':480},{'maxAttempts':0},{'dayStart':1440},{'maxAttempts':11},{'nightEnd':'06:00'}]:
   d=source();d['shiftSchedule'].update(update)
   with self.assertRaises(abvm.AbvmError):abvm.Compiler().compile_amsj(d,ROUTES)
 def test_require_early_checks_and_authored_switch_routes(self):
  for tab in ('Desktop','Startup','SwitchToDay','SwitchToNight'):
   d=source();d['pipelines'][tab]=[]
   with self.assertRaises(abvm.AbvmError):abvm.Compiler().compile_amsj(d,ROUTES)
  d=source();d['pipelines']['Desktop'].insert(0,{'Type':'keystroke','Props':{'key':'1'}})
  with self.assertRaises(abvm.AbvmError):abvm.Compiler().compile_amsj(d,ROUTES)
 def test_identity_only_compatible(self):
  d=source();d['shiftSchedule']['enabled']=False
  p=abvm.Compiler().compile_amsj(d,ROUTES);v=abvm.Verifier.verify(p.image)
  self.assertTrue(all(v.const(i.a,11)[:4]==b'SFT1' for i in v.instructions if i.op==32))
 def test_actual_flash_store_migration_and_transaction(self):
  with tempfile.TemporaryDirectory() as t:
   d=pathlib.Path(t);(d/'hardware').mkdir();(d/'pico').mkdir()
   (d/'hardware/flash.h').write_text('#include <stdint.h>\n#include <stddef.h>\n#define FLASH_SECTOR_SIZE 4096u\n#define FLASH_PAGE_SIZE 256u\n#define PICO_FLASH_SIZE_BYTES 2097152u\nextern uint8_t *test_flash;\n#define XIP_BASE ((uintptr_t)test_flash)\nvoid flash_range_erase(uint32_t,size_t);\nvoid flash_range_program(uint32_t,const uint8_t *,size_t);\n')
   (d/'hardware/sync.h').write_text('#include <stdint.h>\nuint32_t save_and_disable_interrupts(void);\nvoid restore_interrupts(uint32_t);\n');(d/'pico/stdlib.h').write_text('')
   exe=self.build(d,ROOT/'firmware/abvm/tests/shift_schedule_store_smoke.c',include=[d]);subprocess.run([str(exe)],check=True)
 def test_actual_cycle_switch_reset_only_on_confirmation(self):
  with tempfile.TemporaryDirectory() as t:
   d=pathlib.Path(t);s=(ROOT/'firmware/abvm/tests/abvm_cycle_runtime_smoke.c').read_text()
   s=s.replace('uint8_t calibration_store_shift_target(void){return 0u;}','static uint8_t shift_target,shift_attempts;\nuint8_t calibration_store_shift_target(void){return shift_target;}')
   s=s.replace('bool calibration_store_shift_begin(uint8_t target,uint8_t maximum){(void)target;(void)maximum;return false;}','bool calibration_store_shift_begin(uint8_t target,uint8_t maximum){if(shift_attempts>=maximum)return false;++shift_attempts;shift_target=target;marker_armed=true;return true;}')
   s=s.replace('bool calibration_store_shift_complete(void){return true;}','bool calibration_store_shift_complete(void){shift_attempts=0;shift_target=0;marker_count=0;marker_armed=false;return true;}')
   s=s.replace('    free(image);','''
    cycle_runtime_manual_stop();cycle_runtime_manual_start(90000u);marker_count=3u;
    if(!require(cycle_runtime_begin_shift(16u,1u,2u,90001u),"begin switch")||
       !require(marker_count==3u&&shift_attempts==1u,"switch preserves round count"))return 1;
    (void)cycle_runtime_route_complete(16u,90002u);
    (void)cycle_runtime_service(90003u,true,ARM_HOST_USB_UP,true);
    if(!require(cycle_runtime_service(92003u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_NONE,"warm switch cannot mistake old USB UP for a reboot"))return 1;
    if(!require(cycle_runtime_init(&vm,90003u),"power loss restores pending switch"))return 1;
    (void)cycle_runtime_service(90004u,true,ARM_HOST_USB_UP,true);
    if(!require(cycle_runtime_service(92004u,true,ARM_HOST_USB_UP,true)==CYCLE_ACTION_START_STARTUP,"corrective restart starts Startup"))return 1;
    cycle_runtime_begin_startup();
    if(!require(marker_count==3u,"still not reset before verification")||
       !require(cycle_runtime_begin_shift(16u,1u,2u,92005u),"retry second switch")||
       !require(!cycle_runtime_begin_shift(16u,1u,2u,92006u),"third switch blocked"))return 1;
    (void)cycle_runtime_route_complete(16u,92007u);
    if(!require(cycle_runtime_service(272007u,true,ARM_HOST_USB_UP,false)==CYCLE_ACTION_SHIFT_STALLED,"missing reboot bounded wait"))return 1;
    cycle_runtime_begin_startup();
    if(!require(cycle_runtime_shift_confirmed(272008u),"verified destination")||
       !require(marker_count==0u&&!shift_attempts&&!shift_target,"atomic reset to round one")||
       !require(cycle_runtime_route_complete(cycle_runtime_startup_route(),272009u),"verified Startup continues ordered login"))return 1;
    free(image);''')
   c=d/'cycle.c';c.write_text(s);exe=self.build(d,c,[ROOT/'firmware/abvm/src/abvm_vm.c',ROOT/'firmware/abvm/pico/cycle_runtime.c']);p=d/'p.abp';p.write_bytes(abvm.Compiler().compile_amsj(source(),ROUTES).image);subprocess.run([str(exe),str(p)],check=True)
 def test_actual_adapter_blocks_wrong_user_and_resets_verified_destination(self):
  with tempfile.TemporaryDirectory() as t:
   d=pathlib.Path(t);main=(ROOT/'firmware/abvm/pico/main.c').read_text()
   adapter=main[main.index('static void fail_shift_check('):main.index('static void service_game_buffs(')]
   c=d/'adapter.c';c.write_text((ROOT/'firmware/abvm/tests/shift_schedule_adapter_smoke.c').read_text().replace('/* PRODUCTION_ADAPTER */',adapter))
   exe=self.build(d,c,[ROOT/'firmware/abvm/src/abvm_vm.c',ROOT/'firmware/abvm/pico/hid_keyboard.c',ROOT/'firmware/abvm/pico/shift_identity_runtime.c'],[ROOT/'firmware/abvm/tests/pico_stub'])
   data=source();data['pipelines']['Desktop']=data['pipelines']['Desktop'][:1]+[{'Type':'typeText','Props':{'text':'Q','hmin':10,'hmax':10},'Children':[]}]
   p=d/'p.abp';p.write_bytes(abvm.Compiler().compile_amsj(data,ROUTES).image)
   for mode in range(7):subprocess.run([str(exe),str(p),str(mode)],check=True)
 def test_old_identity_template_cannot_run_schedule(self):
  sys.path.insert(0,str(ROOT/'firmware/abvm/tests'));from test_abvm_uf2 import make_uf2
  from abvm_uf2 import inject,Uf2Error
  program=abvm.Compiler().compile_amsj(source(),ROUTES).image
  with self.assertRaisesRegex(Uf2Error,'lacks scheduled shift'):
   inject(make_uf2(b'OK|SHIFT-CHALLENGE|',32768),program)
if __name__=='__main__':unittest.main()
