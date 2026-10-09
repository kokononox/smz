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
        self.assertIn('tud_remote_wakeup()',main)
        self.assertIn('pico_remote_wakeup_en=remote_wakeup_en;',main)
        self.assertIn('calibration_store_wake_get(&wake_store_last)',main)
        self.assertIn('wake_store_service(now,false);',main)
    def test_two_independent_wake_sources_are_armed_by_the_host(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_USE_PICO_WAKEUP 1',main)
        self.assertIn('#define WAKE_USE_ARM_PULSE 1',main)
        # The Pico path must never drive resume on a bus the host did not suspend
        # or did not arm, because that would be a protocol violation.
        self.assertIn('if(!pico_usb_suspended||!pico_remote_wakeup_en) return false;',main)
        self.assertIn('wake_pulse_inflight=result==ARM_MOUSE_ACCEPTED;',main)
        self.assertIn('if(pico||result==ARM_MOUSE_ACCEPTED) {',main)
    def test_operator_test_command_and_status_fields(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('!strncmp(line, "WAKE!", 5)',main)
        self.assertIn('wake_scheduler_arm_at(&wake_scheduler, now,',main)
        self.assertIn('wake_scheduler_arm_dry(&wake_scheduler, now,',main)
        self.assertIn('if (!strcmp(bang + 1, "dry")) dry = true;',main)
        self.assertIn('WAKE!s-WAKE!s!dry-WAKE!OFF',main)
        self.assertIn('|pico-usb=%u|pico-rw=%u|wake=%u',main)
        self.assertIn('|wake-recovery=%u|wake-host=%u',main)
    def test_dry_test_arm_wakes_without_starting_a_round(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('bool dry=wake_scheduler.dry;',main)
        self.assertIn('skipped=manual-dry',main)
        block=re.search(r'if\(dry\) \{(.*?)\n        \}',main,re.S)
        self.assertIsNotNone(block)
        # The safe test path must leave before the authored round is started.
        self.assertIn('return;',block.group(1))
        self.assertNotIn('start_control',block.group(1))
    def test_persisted_wake_record_is_versioned(self):
        store=(ROOT/'firmware/abvm/pico/calibration_store.c').read_text()
        self.assertIn('#define CAL_VERSION 6u',store)
        self.assertIn('typedef struct LegacyCalibrationPayloadV5',store)
        self.assertIn('legacy_v5_record_valid(v5a,binding)',store)
        self.assertIn('offsetof(CalibrationPayload,wake_flags)',store)
        self.assertIn('_Static_assert(sizeof(CalibrationRecord) <= FLASH_PAGE_SIZE',store)
        # A record that has not changed must never erase a flash sector.
        self.assertIn('current.payload.wake_next_start==state->next_start)return true;',store)
    def test_recovery_pulse_is_bounded_and_never_fires_at_an_awake_host(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_RECOVERY_MAX_ATTEMPTS 3u',main)
        self.assertIn('#define WAKE_HOST_SAMPLE_TIMEOUT_MS 5000u',main)
        self.assertIn('wake_scheduler_recovery_needed(',main)
        self.assertIn('reason=host-up',main)
        self.assertIn('reason=limit|attempts=%u',main)
        # The pulse waits for a real host sample before it decides, so an awake
        # host is cancelled instead of nudged by a stray mouse report.
        self.assertIn('wake_recovery_deadline=now_ms()+WAKE_HOST_SAMPLE_TIMEOUT_MS;',main)
        self.assertIn('if(!arm_uart_host_usb_seen()&&!pico_usb_suspended&&',main)
        # The attempt must be persisted before the pulse is sent, otherwise a
        # brownout loop could turn into a wake storm.
        self.assertLess(main.index('calibration_store_wake_set(&state)'),
                        main.index('bool pico=wake_pulse_pico();'))
        self.assertIn('(void)calibration_store_wake_recovery_reset();',main)
    def test_recovery_boot_cannot_hang_on_usb_enumeration(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_MOUNT_TIMEOUT_MS 5000u',main)
        self.assertIn('(int32_t)(now_ms()-(mount_started+WAKE_MOUNT_TIMEOUT_MS))>=0) break;',main)
    def test_wake_clears_the_windows_lock_screen(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_DISMISS_ENABLED 1',main)
        self.assertIn('#define WAKE_DISMISS_KEY 13u',main)
        self.assertIn('#define WAKE_DISMISS_PRESSES 3u',main)
        self.assertIn('#define WAKE_DISMISS_TIMEOUT_MS 20000u',main)
        self.assertIn('hid_keyboard_submit_trigger(WAKE_DISMISS_KEY,40u,90u,now)',main)
        # The lock screen goes away on Enter alone: no pointer click, because the
        # authored macro language has no click opcode and the mouse must not move.
        self.assertNotIn('WAKE_DISMISS_CLICK_COMMAND',main)
        self.assertIn('if(wake_dismiss_step<WAKE_DISMISS_PRESSES)',main)
        self.assertIn('wake_dismiss_step++;',main)
        dismiss=main[main.index('if(wake_phase==WAKE_PHASE_DISMISS) {'):
                    main.index('wake_phase=WAKE_PHASE_SETTLE;wake_deadline=now;',
                               main.index('if(wake_phase==WAKE_PHASE_DISMISS) {'))]
        self.assertNotIn('MCLICK',dismiss)
        # The dismiss is only ever reached for a host this board actually woke, and
        # it runs before the authored round takes over.
        self.assertIn('if(wake_woke_host&&!wake_dismiss_done)',main)
        self.assertIn('wake_woke_host=false;wake_dismiss_done=false;wake_dismiss_step=0u;wake_dismiss_started=0u;',main)
        self.assertLess(main.index('if(wake_woke_host&&!wake_dismiss_done)'),
                        main.index('printf("EVT|WAKE|state=start|attempt=%u\\n",wake_attempts);'))
        # Every Enter is logged, and none may hold up the shift.
        self.assertIn('EVT|WAKE|state=dismiss|step=enter|n=%u',main)
        self.assertIn('ERR|WAKE|dismiss|key=%u|n=%u',main)
        self.assertIn('wake_dismiss_done=true;',main)

    def test_dismiss_phase_can_never_park_the_wake_machine(self):
        # A key report only goes out while the host keeps this board's port
        # resumed, and the host can resume through the Arduino board instead.
        # Waiting for that endpoint forever parked the phase machine, so a later
        # arm could never reach its pulse and the shift was lost.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('wake_dismiss_started=now;',main)
        self.assertIn('(int32_t)(now-wake_dismiss_started-WAKE_DISMISS_TIMEOUT_MS)>=0',main)
        self.assertIn('ERR|WAKE|dismiss|stall|step=%u|usb=%u',main)
        # The cap releases the phase instead of ending the shift.
        stall=main.index('ERR|WAKE|dismiss|stall')
        self.assertLess(stall,main.index('wake_dismiss_step=WAKE_DISMISS_PRESSES;'))
        self.assertLess(main.index('wake_dismiss_step=WAKE_DISMISS_PRESSES;'),
                        main.index('wake_dismiss_done=true;'))
        # A suspended port is not a dead end: ask for the port back and let the
        # board that certainly resumed press the key.
        self.assertIn('if(!pico_usb_suspended) return;',main)
        self.assertIn('(void)wake_pulse_pico();',main)
        self.assertIn('#define WAKE_DISMISS_ARM_FALLBACK 1',main)
        self.assertIn('#define WAKE_DISMISS_ARM_COMMAND "KCOMBO|13,40,90"',main)
        self.assertIn('arm_uart_mouse_submit_internal(WAKE_DISMISS_ARM_COMMAND,now)',main)
        self.assertIn('EVT|WAKE|state=dismiss|step=enter|n=%u|via=arm',main)
        # A fresh manual arm starts from a clean phase machine.
        arm_reset=main.index('wake_phase=WAKE_PHASE_IDLE;wake_pulse_inflight=false;\n'
                             '                wake_woke_host=false;wake_dismiss_done=false;')
        self.assertLess(arm_reset,main.index('OK|WAKE|manual|in=%lu'))
        self.assertIn('wake_retry_at=now;',main[arm_reset:arm_reset+400])
        # And the phase itself is visible in STATUS.
        self.assertIn('|wake-phase=%u\\n", abvm_status_name(vm.status)',main)
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
