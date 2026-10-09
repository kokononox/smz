import copy
import hashlib
import pathlib
import subprocess
import sys
import tempfile
import unittest
ROOT=pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/"tools"))
import abvm
def node(kind,props=None):
    return {"Type":kind,"Props":props or {},"Children":[]}
def source():
    check=node("shiftCheck",{"key":"4","modWin":True,"holdMin":90,"holdMax":160})
    text=node("typeText",{"textScope":"shift","text":"G","textDay":"D","textNight":"N","hmin":10,"hmax":10})
    return {"nativeShift":{"dayHash":hashlib.sha256(b"DAYUSER").hexdigest(),"nightHash":hashlib.sha256(b"NIGHTUSER").hexdigest()},
            "pipelines":{"Desktop":[check,text],"Startup":[copy.deepcopy(check),copy.deepcopy(text)],"Game":[copy.deepcopy(text)]}}
class ShiftIdentityTests(unittest.TestCase):
    def compile_c(self,directory,harness,extra):
        exe=directory/"test"
        subprocess.run(["cc","-std=c11","-Wall","-Wextra","-Werror",
            "-I"+str(ROOT/"firmware/abvm/tests/pico_stub"),
            "-I"+str(ROOT/"firmware/abvm/include"),"-I"+str(ROOT/"firmware/abvm/pico"),
            str(harness),str(ROOT/"firmware/abvm/pico/shift_identity_runtime.c"),*map(str,extra),"-o",str(exe)],check=True)
        return exe
    def test_real_core(self):
        with tempfile.TemporaryDirectory() as temp:
            d=pathlib.Path(temp)
            exe=self.compile_c(d,ROOT/"firmware/abvm/tests/shift_identity_runtime_smoke.c",[])
            subprocess.run([str(exe)],check=True)
    def test_actual_adapter_and_pico_typing(self):
        with tempfile.TemporaryDirectory() as temp:
            d=pathlib.Path(temp)
            main=(ROOT/"firmware/abvm/pico/main.c").read_text()
            adapter=main[main.index("static void fail_shift_check("):main.index("static void service_game_buffs(")]
            harness=(ROOT/"firmware/abvm/tests/shift_identity_adapter_smoke.c").read_text()
            c=d/"adapter.c";c.write_text(harness.replace("/* PRODUCTION_ADAPTER */",adapter))
            exe=self.compile_c(d,c,[ROOT/"firmware/abvm/src/abvm_vm.c",ROOT/"firmware/abvm/pico/hid_keyboard.c"])
            program=d/"program.abp";program.write_bytes(abvm.Compiler().compile_amsj(source(),("Desktop","Startup","Game")).image)
            try:subprocess.run([str(exe),str(program)],check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            except subprocess.CalledProcessError as e:
                self.fail(e.stderr.decode()+e.stdout.decode())
    def test_username_config_required_and_distinct(self):
        for config in ({},{"dayHash":"11"*32,"nightHash":"11"*32}):
            data=source();data["nativeShift"]=config
            with self.assertRaises(abvm.AbvmError):abvm.Compiler().compile_amsj(data,("Desktop",))
    def test_shift_text_both_fields_required(self):
        data=source();data["pipelines"]["Game"][0]["Props"]["textNight"]=""
        with self.assertRaisesRegex(abvm.AbvmError,"both day and night"):
            abvm.Compiler().compile_amsj(data,("Game",))
    def test_check_only_before_game(self):
        data=source();data["pipelines"]["Game"].insert(0,node("shiftCheck"))
        with self.assertRaisesRegex(abvm.AbvmError,"Desktop or Startup"):
            abvm.Compiler().compile_amsj(data,("Game",))
    def test_legacy_template_rejected(self):
        sys.path.insert(0,str(ROOT/"firmware/abvm/tests"))
        from test_abvm_uf2 import make_uf2
        from abvm_uf2 import inject,Uf2Error
        data=abvm.Compiler().compile_amsj(source(),("Desktop",)).image
        with self.assertRaisesRegex(Uf2Error,"lacks the Pico shift"):
            inject(make_uf2(b"ABP1old",16384),data)
if __name__=="__main__":unittest.main()