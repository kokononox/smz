import pathlib,re,subprocess,sys,tempfile,unittest
ROOT=pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/'tools'))
class ShiftWakeTests(unittest.TestCase):
    def test_scheduler_core(self):
        with tempfile.TemporaryDirectory() as t:
            exe=pathlib.Path(t)/'wake'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/pico'),
                str(ROOT/'firmware/abvm/pico/wake_scheduler.c'),
                str(ROOT/'firmware/abvm/tests/wake_scheduler_smoke.c'),
                '-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
    def test_main_wiring(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('wake_scheduler_init(&wake_scheduler,WAKE_LEAD_MINUTES)',main)
        self.assertIn('wake_scheduler_sync(&wake_scheduler,now,minute)',main)
        self.assertIn('wake_scheduler_configure(&wake_scheduler,shift_identity.schedule_enabled',main)
        self.assertIn('service_shift_check(now); service_wake(now);',main)
        self.assertIn('arm_uart_host_usb_state()==ARM_HOST_USB_UP',main)
        self.assertIn('arm_uart_mouse_submit_internal(WAKE_PULSE_COMMAND,now)',main)
        self.assertIn('wake_attempts_reset();',main)
    def test_wake_code_stays_out_of_the_adapter_slices(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        for start,end in (("static void fail_shift_check(","static void service_game_buffs("),
                          ("static void service_game_buffs(","static void service_vm(")):
            block=main[main.index(start):main.index(end)]
            self.assertNotIn('wake_scheduler',block)
            self.assertNotIn('service_wake',block)
    def test_pulse_respects_the_three_pixel_contract(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        m=re.search(r'#define WAKE_PULSE_COMMAND "MMOVE\|(-?\d+),(-?\d+),rel,\d+"',main)
        self.assertIsNotNone(m)
        dx,dy=int(m.group(1)),int(m.group(2))
        self.assertLessEqual(dx*dx+dy*dy,9)
    def test_lead_and_settle_bounds(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        lead=re.search(r'#define WAKE_LEAD_MINUTES (\d+)u',main)
        settle=re.search(r'#define WAKE_SETTLE_MS (\d+)u',main)
        attempts=re.search(r'#define WAKE_MAX_ATTEMPTS (\d+)u',main)
        self.assertIsNotNone(lead);self.assertIsNotNone(settle);self.assertIsNotNone(attempts)
        self.assertTrue(0<int(lead.group(1))<=15)
        self.assertTrue(int(settle.group(1))>=5000)
        self.assertTrue(1<=int(attempts.group(1))<=5)
if __name__=='__main__':unittest.main()
