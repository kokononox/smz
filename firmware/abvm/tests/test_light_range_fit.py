import json, pathlib, subprocess, sys, tempfile, unittest
ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import abvm
class FitTest(unittest.TestCase):
    def test_real_c_fit(self):
        with tempfile.TemporaryDirectory() as d:
            exe = pathlib.Path(d) / 'fit'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/pico'),str(ROOT/'firmware/abvm/tests/light_range_fit_smoke.c'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
    def test_asymmetric_bounds_in_compiled_guard(self):
        source=json.loads((ROOT/'firmware/abvm/tests/abvm_guard_smoke.amsj').read_text())
        profile=source['nativeGuard']['profiles'][0]
        profile['luxMin']=95.0;profile['luxMax']=102.5
        routes=('Desktop','Restart','Startup','LoginOrDc','Dc','CharacterDashboard','EnteringGameLoading','Game','Targeted','TargetedRepeat','Whisper','WhisperRepeat','Finish')
        image=abvm.Verifier.verify(abvm.Compiler().compile_amsj(source,routes).image)
        raw=next(data for kind,_,data in image.constants if kind==abvm.CONST_GUARD)
        item=abvm.GUARD_PROFILE.unpack_from(raw,abvm.GUARD_HEADER.size)
        self.assertEqual((item[3],item[4]),(950,1025))
    def test_physical_runtime(self):
        with tempfile.TemporaryDirectory() as d:
            exe = pathlib.Path(d) / 'runtime'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/include'),'-I'+str(ROOT/'firmware/abvm/pico'),
                str(ROOT/'firmware/abvm/pico/calibration_runtime.c'),
                str(ROOT/'firmware/abvm/tests/light_calibration_runtime_smoke.c'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
if __name__ == '__main__': unittest.main()
