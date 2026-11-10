import pathlib, subprocess, tempfile, unittest
ROOT = pathlib.Path(__file__).resolve().parents[3]
class FitTest(unittest.TestCase):
    def test_real_c_fit(self):
        with tempfile.TemporaryDirectory() as d:
            exe = pathlib.Path(d) / 'fit'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/pico'),str(ROOT/'firmware/abvm/tests/light_range_fit_smoke.c'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
    def test_physical_runtime(self):
        with tempfile.TemporaryDirectory() as d:
            exe = pathlib.Path(d) / 'runtime'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/include'),'-I'+str(ROOT/'firmware/abvm/pico'),
                str(ROOT/'firmware/abvm/pico/calibration_runtime.c'),
                str(ROOT/'firmware/abvm/tests/light_calibration_runtime_smoke.c'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
if __name__ == '__main__': unittest.main()
