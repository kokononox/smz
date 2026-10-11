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
    def test_a_consumed_test_arm_hands_the_deadline_back_to_the_schedule(self):
        sched=(ROOT/'firmware/abvm/pico/wake_scheduler.c').read_text()
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('bool wake_scheduler_rearm(WakeScheduler *w, uint32_t now)',sched)
        # The guard must come before the sync: re-arming inside the lead time would
        # fall back to "one minute from now" and turn a test into a wake loop.
        self.assertLess(sched.index('<= w->lead_minutes) return false;'),
                        sched.index('return wake_scheduler_sync(w, now, minute);'))
        self.assertIn('wake_scheduler_rearm(&wake_scheduler,now)',main)
        self.assertIn('EVT|WAKE|rearmed|window=%02u:%02u|in=%lu',main)
        # Only the consumed-deadline path re-arms.  WAKE!OFF, an exhausted attempt
        # budget and a failed pulse all stay disarmed on purpose.
        consumed=main.index('wake_scheduler_rearm(&wake_scheduler,now)')
        self.assertGreater(consumed,main.index('bool dry=wake_scheduler.dry;'))
        disarm=main.rindex('wake_scheduler_disarm(&wake_scheduler);',0,consumed)
        self.assertLess(consumed-disarm,700)
        # A re-arm that is skipped must name its reason.  A board left with no
        # deadline writes no line at all, which is the one failure an operator
        # cannot see in the log.
        self.assertIn('EVT|WAKE|rearm|skipped|reason=%s|lead=%u|wall=%s',main)
        for reason in ('"schedule-off"','"no-clock"','"inside-lead"'):
            self.assertIn(reason,main)

    def test_the_window_test_reclaims_the_clock_from_a_stale_or_missing_arm(self):
        script=(ROOT/'tools/wake-test/wake-test.ps1').read_text(encoding='utf-8-sig')
        # A board left on a consumed test arm, or on no deadline at all, used to
        # dead-end the window test with an instruction to run another tool.  The
        # tool now re-sends the clock itself, because TIME! is the one command that
        # hands the deadline back to the authored schedule.
        self.assertIn('function Read-WakeState($text)',script)
        self.assertIn('function Test-OnRealWindow($state)',script)
        self.assertIn('$state.manual -or $state.dry',script)
        self.assertIn("$clockReply -notmatch 'OK\|TIME'",script)
        self.assertIn("$clockReply -match 'armed=0\|schedule=0'",script)
        # A board whose firmware predates TIME! answers nothing at all, and that
        # silence must name the missing firmware instead of looking like a fault.
        self.assertIn('فریم\u200cور جدید روی برد نیست',script)
        # The refusal must not send the operator to Classroom Studio: the round runs
        # on the board, and the clock comes from wake-set-clock.cmd.
        self.assertIn('wake-set-clock.cmd',script)
        self.assertNotIn('Classroom Studio را باز کن',script)

    def test_the_board_learns_its_schedule_at_boot(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        # The authored windows travel inside the flashed program, so the board
        # must not depend on a round reaching its shift check to learn them.
        self.assertIn('abvm_find_constant(&vm,ABVM_CONST_SHIFT',main)
        self.assertIn('EVT|WAKE|schedule|source=program',main)
        self.assertIn('EVT|WAKE|schedule|source=none',main)
        boot=main.index('abvm_find_constant(&vm,ABVM_CONST_SHIFT')
        self.assertLess(main.index('wake_scheduler_init(&wake_scheduler,WAKE_LEAD_MINUTES)'),boot)
        self.assertLess(boot,main.index('tusb_init();'))

    def test_a_one_shot_clock_stamp_needs_no_host_software(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('!strncmp(line, "TIME!", 5)',main)
        self.assertIn('wake_scheduler_sync(&wake_scheduler, now,',main)
        self.assertIn('ERR|ARG|TIME',main)
        self.assertIn('TIME!HH:MM',main)
        # A fresh stamp is a fresh clock, so it retires the recovery budget the
        # same way an accepted bridge sample does.
        stamp=main.index('!strncmp(line, "TIME!", 5)')
        self.assertIn('calibration_store_wake_recovery_reset();',
                      main[stamp:stamp+1800])

    def test_the_clock_stamp_tool_is_clickable(self):
        tool=ROOT/'tools'/'wake-test'
        self.assertIn('clock',(tool/'wake-test.ps1').read_text(encoding='utf-8-sig'))
        self.assertIn('clock',(tool/'wake-set-clock.cmd').read_text(encoding='ascii'))

    def test_persisted_wake_record_is_versioned(self):
        store=(ROOT/'firmware/abvm/pico/calibration_store.c').read_text()
        self.assertIn('#define CAL_VERSION 7u',store)
        self.assertIn('typedef struct LegacyCalibrationPayloadV5',store)
        self.assertIn('legacy_v5_record_valid(v5a,binding)',store)
        self.assertIn('offsetof(CalibrationPayload,wake_flags)',store)
        # The learned cold start lands in the two bytes v6 left as trailing
        # padding, so a v6 record and a v7 record are the same size and the
        # migration copies the named prefix instead of reading the padding.
        self.assertIn('typedef struct LegacyCalibrationPayloadV6',store)
        self.assertIn('legacy_v6_record_valid(v6a,binding)',store)
        self.assertIn('offsetof(CalibrationPayload,host_boot_s)',store)
        self.assertIn('_Static_assert(sizeof(CalibrationPayload)==sizeof(LegacyCalibrationPayloadV6),',store)
        self.assertIn('_Static_assert(sizeof(CalibrationRecord) <= FLASH_PAGE_SIZE',store)
        # A record that has not changed must never erase a flash sector.
        self.assertIn('current.payload.wake_next_start==state->next_start&&',store)
        self.assertIn('current.payload.host_boot_s==state->host_boot_s)return true;',store)
    def test_the_power_button_waits_out_the_machines_own_post(self):
        # A host that is still in POST looks exactly like a host that is off: until
        # its own USB stack comes up, nothing of ours is on its bus at all.  A board
        # that powered up together with the machine -- the power cut that restarted
        # both -- would therefore read "off" a few seconds in and press a button
        # into a running POST, and a machine answers that by shutting down again,
        # which undoes the very BIOS setting that brought it back.  The grace is
        # this machine's own cold start, learned and persisted, so nothing here
        # belongs to one vendor's hardware: what is waited out is the POST.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        store=(ROOT/'firmware/abvm/pico/calibration_store.c').read_text()
        self.assertIn('#define POWER_BUTTON_GRACE_DEFAULT_MS 60000u',main)
        self.assertIn('#define POWER_BUTTON_GRACE_MIN_MS 30000u',main)
        self.assertIn('#define POWER_BUTTON_GRACE_MAX_MS 150000u',main)
        # The floor sits above this board's own enumeration time: a board that
        # restarted under a running host must not teach itself a cold start it
        # never watched, and the wait must never shrink into a press on a POST.
        self.assertIn('#define HOST_BOOT_LEARN_MIN_S 8u',main)
        self.assertIn('static uint32_t power_button_grace_ms(void)',main)
        self.assertIn('reason=boot-grace',main)
        self.assertIn('power_button_boot_at=now_ms();',main)
        # A stored value that could not have come from a POST is nothing learned,
        # so a migrated or corrupted record can only ever lengthen the wait.
        self.assertIn('if(stored<HOST_BOOT_LEARN_MIN_S||stored>HOST_BOOT_LEARN_MAX_S) stored=0u;',main)
        # The sample is the machine's own power-on-to-USB time, and the origin
        # moves with a press so it stays the same quantity either way.
        self.assertIn('EVT|PWRBTN|host-boot|learned-s=%u|grace=%u',main)
        self.assertIn('host_boot_origin_at=now;',main)
        self.assertIn('host_boot_learned_s=0u;host_boot_measured=false;power_button_grace_logged=false;',main)
        # It travels with the decision it belongs to, or the next power cut would
        # find the record empty and wait the default all over again.
        self.assertIn('if(host_boot_learned_s>state.host_boot_s) state.host_boot_s=host_boot_learned_s;',main)
        self.assertIn('state.host_boot_s!=wake_store_last.host_boot_s;',main)
        self.assertIn('uint16_t host_boot_s;',store)
        self.assertIn('state->host_boot_s=current.payload.host_boot_s;',store)
        # The wait spends no attempt, and it sits before the budget is charged:
        # only a press costs one.
        recovery=main.index('static void service_wake_recovery(uint32_t now)')
        self.assertLess(main.index('bool host_known_asleep=',recovery),
                        main.index('reason=boot-grace',recovery))
        self.assertLess(main.index('reason=boot-grace',recovery),
                        main.index('calibration_store_wake_set(&state)',recovery))
        # A bus the Arduino board reports suspended is a host that is present and
        # asleep, so the press that wakes it is never delayed.
        self.assertIn('arm_uart_host_usb_state()==ARM_HOST_USB_SUSPEND;',main)
        self.assertIn('if(host_absent&&!host_known_asleep) {',main)
        self.assertIn('|host-boot-s=%u|pwr-grace=%u',main)
    def test_recovery_pulse_is_bounded_and_never_fires_at_an_awake_host(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_RECOVERY_MAX_ATTEMPTS 3u',main)
        self.assertIn('#define WAKE_HOST_SAMPLE_TIMEOUT_MS 5000u',main)
        self.assertIn('wake_scheduler_recovery_needed(',main)
        self.assertIn('reason=host-up',main)
        # The helper board's verdict and this board's own mount state travel with
        # the decision, because the console is the same USB the decision is about.
        self.assertIn('EVT|HOST|usb=%s|mounted=%u|at-s=%u',main)
        self.assertIn('reason=host-up|arm-usb=%u|mounted=%u|at-s=%u',main)
        # The only signal that survives a machine that is off is the board's own
        # LED.  It burns dim and steady while the board runs -- a pulse every two
        # seconds was both brighter and a moving light in the room -- and only a
        # power-up or a real press is allowed a bright flash.
        self.assertIn('#define STATUS_LED_PIN 25u',main)
        self.assertIn('#define STATUS_LED_DIM_LEVEL 20u',main)
        self.assertIn('gpio_set_function(STATUS_LED_PIN,GPIO_FUNC_PWM);',main)
        self.assertIn('pwm_set_gpio_level(STATUS_LED_PIN,level);',main)
        self.assertIn('led_flash(now,STATUS_LED_PRESS_MS);',main)
        self.assertIn('led_flash(now,STATUS_LED_BOOT_MS);',main)
        self.assertIn('if(led_bright&&(int32_t)(now-led_bright_until)>=0) led_dim();',main)
        self.assertNotIn('gpio_put(STATUS_LED_PIN',main)
        # The level is judged by eye, so it is settable from the console and the
        # default is documented rather than guessed at flash time.
        self.assertIn('!strncmp(line, "LED!", 4)',main)
        self.assertIn('led_set_dim((uint16_t)level);',main)
        self.assertIn('printf("OK|LED|dim=%u\\n",(unsigned)level);',main)
        self.assertIn('PWRBTN-ms,LED!0-999',main)
        # A host that is off must not park the board in the boot wait: that is the
        # machine whose power button it may have to press, and the lines held back
        # are replayed from RAM anyway.
        self.assertIn('mount_started+WAKE_MOUNT_TIMEOUT_MS))>=0) break;',main)
        self.assertNotIn('wake_recovery_phase!=WAKE_RECOVERY_IDLE&&',main)
        self.assertIn('service_status_led(now);',main)
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
    def test_a_clockless_power_on_brings_the_host_up_once(self):
        # A power cut loses the wall anchor *and* leaves no owed wake behind, so
        # the persisted decision cannot speak for it: the board would come back
        # with a schedule it can never arm, and the silence would be
        # indistinguishable from a healthy board.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        sched=(ROOT/'firmware/abvm/pico/wake_scheduler.c').read_text()
        self.assertIn('bool wake_scheduler_boot_clock_needed(bool enabled, bool synced,',sched)
        self.assertIn('return enabled && !synced && wake_scheduler_pulse_budget_left(',sched)
        self.assertIn('wake_scheduler_boot_clock_needed(wake_scheduler.enabled,',main)
        self.assertIn('WAKE_RECOVERY_REASON_CLOCK',main)
        # Both reasons spend one persisted budget, and the pulse line names the
        # reason so the operator can tell a power cut from an owed wake.
        self.assertIn('wake_scheduler_pulse_budget_left(state.recovery_attempts,'
                      'WAKE_RECOVERY_MAX_ATTEMPTS)',main)
        self.assertIn('|wall=%s|reason=%s',main)
        self.assertIn('EVT|WAKE|recovery|armed|reason=%s|target=%02u:%02u|attempts=%u',main)
        self.assertIn('|recovery-reason=%s',main)
        # The decision is taken where the schedule is already known and before USB
        # attaches, exactly like the owed-wake recovery it sits beside.
        boot=main.index('wake_scheduler_boot_clock_needed(wake_scheduler.enabled,')
        self.assertLess(main.index('abvm_find_constant(&vm,ABVM_CONST_SHIFT'),boot)
        self.assertLess(boot,main.index('tusb_init();'))
        # A clock acquisition ends on the sample it was pulsing for: the host that
        # can send it is up by definition, so it never spends a pulse on it.
        self.assertIn('if(wake_recovery_reason==WAKE_RECOVERY_REASON_CLOCK&&'
                      'wake_scheduler.synced) {',main)
        self.assertIn('EVT|WAKE|recovery|skipped|reason=clock',main)
        # A schedule that needs a clock with no budget left to fetch one must say
        # so at boot; a board that can never arm its first window otherwise looks
        # exactly like a healthy one.
        self.assertIn('else if (wake_scheduler.enabled&&!wake_scheduler.synced)',main)
        # Every exit from the phase clears the reason, so a stale reason can never
        # be reported against a later recovery.
        self.assertEqual(main.count('wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;'),
                         main.count('wake_recovery_phase=WAKE_RECOVERY_IDLE;'))
    def test_a_powered_off_machine_is_reached_by_its_own_power_button(self):
        # A PC in soft-off cannot be woken over USB at all: remote wake-up only
        # resumes a bus the host suspended.  The one line that still reaches it is
        # its own power button, kept alive by the ATX standby rail, and an
        # optocoupler across the front-panel header turns that button into a
        # contact this board can close without joining the two grounds.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define POWER_BUTTON_ENABLED 1',main)
        self.assertIn('#define POWER_BUTTON_PIN ',main)
        # A press that outlives the bound is a forced power-off, so the pulse is
        # clamped on both sides and released from the main loop.
        self.assertIn('#define POWER_BUTTON_MIN_MS 100u',main)
        self.assertIn('#define POWER_BUTTON_MAX_MS 1500u',main)
        self.assertIn('static void service_power_button(uint32_t now)',main)
        self.assertLess(main.index('service_cdc(now);'),
                        main.index('service_power_button(now);'))
        # The line is driven low before the USB device or any actor exists, and the
        # optocoupler is held dark across the direction change: a glitch here is a
        # real press on a real machine.
        self.assertLess(main.index('power_button_init();'),main.index('board_init();'))
        init=main.index('static void power_button_init(void)')
        self.assertLess(main.index('gpio_put(POWER_BUTTON_PIN, 0);',init),
                        main.index('gpio_set_dir(POWER_BUTTON_PIN, GPIO_OUT);',init))
        self.assertIn('gpio_pull_down(POWER_BUTTON_PIN);',main)
        # It is pressed only when nothing is on this board's own USB at all: a host
        # that is merely suspended keeps its port mounted and is woken over the bus.
        self.assertIn('bool host_absent=POWER_BUTTON_ENABLED&&!tud_mounted();',main)
        self.assertIn('EVT|WAKE|recovery=power-button|attempt=%u|ms=%u|pressed=%u|reason=%s',main)
        # A powered-off PC takes the Arduino board down with it, so the
        # arm-readiness gate must not swallow the one action that still reaches it.
        self.assertIn('if(!host_absent&&(!arm_uart_mouse_ready()||arm_uart_mouse_busy())) {',main)
        # The press shares the persisted recovery budget and the attempt is stored
        # before it is sent, so a brownout loop cannot become a press storm.
        recovery=main.index('static void service_wake_recovery(uint32_t now)')
        press=main.index('bool pressed=power_button_press(now,POWER_BUTTON_MS);')
        self.assertLess(main.index('calibration_store_wake_set(&state)',recovery),press)
        self.assertIn('#define WAKE_RECOVERY_MAX_ATTEMPTS 3u',main)
        # The operator can exercise the line without cutting the power.
        self.assertIn('!strncmp(line, "PWRBTN", 6)',main)
        self.assertIn('OK|PWRBTN|press|ms=%lu|count=%u',main)
        self.assertIn('ERR|ARG|PWRBTN',main)
        self.assertIn('ERR|PWRBTN|busy',main)
        self.assertIn('PWRBTN-ms',main)
        self.assertIn('|pwr-presses=%u',main)
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
    def test_no_single_source_or_stuck_phase_can_lose_a_wake(self):
        # The Pico resumes the host bus from its own suspended port and needs
        # nothing from the Arduino board, so an unprobed, busy or faulted board
        # must never be able to stop the machine from being woken -- and every
        # skip has to be visible in the log instead of silent.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('if(calibration_runtime_active()) return;',main)
        self.assertIn('if(arm_uart_mouse_ready()&&!arm_uart_mouse_busy()) {',main)
        self.assertIn('bool pico=wake_pulse_pico();',main)
        self.assertLess(main.index('bool pico=wake_pulse_pico();'),
                        main.index('if(arm_uart_mouse_ready()&&!arm_uart_mouse_busy()) {'))
        self.assertIn('ERR|WAKE|pulse|pico-skipped|usb=%u|rw=%u',main)
        self.assertIn('ERR|WAKE|pulse|wait|arm-ready=%u|arm-busy=%u|pico-usb=%u|pico-rw=%u',main)
        self.assertIn('ERR|WAKE|pulse|arm-skipped|ready=%u|busy=%u',main)
        # A recovery that cannot reach the board or read the store gives up
        # instead of blocking the normal wake path behind it.
        self.assertIn('ERR|WAKE|recovery|skipped|reason=arm|ready=%u|busy=%u',main)
        self.assertIn('ERR|WAKE|recovery|skipped|reason=store',main)

    def test_logs_survive_the_host_being_asleep(self):
        # The whole wake decision happens with nobody listening: the CDC is
        # disconnected while the host sleeps.  Lines are held in RAM and
        # replayed, and a long line is written in full instead of being cut at
        # the endpoint buffer.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        cfg=(ROOT/'firmware/abvm/pico/tusb_config.h').read_text()
        self.assertIn('#define CFG_TUD_CDC_TX_BUFSIZE 1024',cfg)
        self.assertIn('#define LOG_REPLAY_BYTES 2048',main)
        self.assertIn('if (!tud_cdc_connected()) { log_replay_add(output, count); return length; }',main)
        self.assertIn('"RPL|begin\\n"',main)
        self.assertIn('"RPL|end\\n"',main)
        self.assertIn('static size_t cdc_write_all(const char *text, size_t length)',main)
        self.assertIn('uint32_t chunk = tud_cdc_write(text + sent, (uint32_t)(length - sent));',main)
        # The replay is bounded and drops the oldest bytes instead of growing.
        self.assertLess(main.index('static void log_replay_add'),main.index('static int cdc_printf'))

    def test_wake_code_stays_out_of_the_adapter_slices(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        # Anchor on the definition, not on the bare name: the forward declaration
        # that lets the cycle stop a round loudly sits far above these slices.
        for start,end in (("static void fail_shift_check(uint32_t now,const char *reason) {",
                           "static void service_game_buffs("),
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
    def test_operator_wake_state_probe(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('!strcmp(line, "WAKE?")',main)
        self.assertIn('OK|WAKE|armed=%u|manual=%u|dry=%u',main)
        self.assertIn('WAKE!s-WAKE!s!dry-WAKE!OFF,WAKE?',main)
        self.assertIn('|due-ms=%ld|attempts=%u|phase=%u|recovery=%u|host=%s',main)
    def test_a_stale_host_awake_report_cannot_eat_the_only_wake(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('#define WAKE_HOST_AWAKE_GRACE_MS 5000u',main)
        self.assertIn('EVT|WAKE|due|host=up|confirm-ms=%u',main)
        self.assertIn('if((int32_t)(now-wake_host_awake_at)<(int32_t)WAKE_HOST_AWAKE_GRACE_MS) return;',main)
        # Every deadline reports the inputs the decision used, once.
        self.assertIn('EVT|WAKE|due|manual=%u|dry=%u|attempts=%u|usb=%s',main)
        self.assertIn('wake_host_awake_at=0u;wake_due_logged=false;',main)
    def test_a_blocked_wake_names_itself(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('ERR|WAKE|blocked|reason=%s|state=%s|phase=%u|recovery=%u',main)
        self.assertIn('wake_report_blocked("recovery",now);',main)
        self.assertIn('wake_report_blocked("round",now);',main)
        self.assertIn('wake_report_blocked("calibration",now);',main)
        self.assertIn('wake_block_logged=false; return; }',main)
    def test_a_freshly_booted_board_can_still_wake(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        # abvm_init leaves ABVM_STATUS_IDLE behind, so gating the wake path on
        # STOPPED disabled it after every flash until start/stop was pressed once.
        self.assertIn('if(vm.status==ABVM_STATUS_RUNNING||vm.status==ABVM_STATUS_PAUSED) {',main)
        self.assertNotIn('if(vm.status!=ABVM_STATUS_STOPPED) {',main)
    def test_a_clock_sample_never_cancels_an_operator_test_arm(self):
        sched=(ROOT/'firmware/abvm/pico/wake_scheduler.c').read_text()
        self.assertIn('if (!w->enabled && w->manual) {',sched)
    def test_lead_and_settle_bounds(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        lead=re.search(r'#define WAKE_LEAD_MINUTES (\d+)u',main)
        settle=re.search(r'#define WAKE_SETTLE_MS (\d+)u',main)
        attempts=re.search(r'#define WAKE_MAX_ATTEMPTS (\d+)u',main)
        self.assertIsNotNone(lead);self.assertIsNotNone(settle);self.assertIsNotNone(attempts)
        self.assertTrue(0<int(lead.group(1))<=15)
        self.assertTrue(int(settle.group(1))>=5000)
        self.assertTrue(1<=int(attempts.group(1))<=5)
    def test_an_owed_press_outlives_the_wait_it_is_given(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        # The record that armed a recovery is the record that authorises its
        # press.  On a machine that is off there is no bus and no arm report, so
        # the live reading is always "the host is not asleep": writing it back
        # over the arming record disarmed the press exactly after the grace had
        # been waited out for it, and the button was never pressed.
        service=main[main.index('static void wake_store_service(uint32_t now,bool force) {'):]
        service=service[:service.index('static bool wake_pulse_pico')]
        self.assertIn('if(wake_recovery_phase!=WAKE_RECOVERY_IDLE) {',service)
        self.assertIn('state.host_asleep=wake_store_last.host_asleep;',service)
        self.assertIn('state.pending=wake_store_last.pending;',service)
        self.assertLess(service.index('state.host_asleep=wake_store_last.host_asleep;'),
                        service.index('bool changed='))
        # The press still re-reads that same record before it fires.
        self.assertIn('wake_scheduler_recovery_needed(state.host_asleep,state.pending,',main)
        self.assertLess(main.index('uint32_t grace=power_button_grace_ms();'),
                        main.index('wake_scheduler_recovery_needed(state.host_asleep,state.pending,'))
    def test_the_last_rounds_window_question_is_answered_by_the_board_itself(self):
        with tempfile.TemporaryDirectory() as t:
            exe=pathlib.Path(t)/'shift'
            subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror',
                '-I'+str(ROOT/'firmware/abvm/pico'),
                str(ROOT/'firmware/abvm/pico/shift_identity_runtime.c'),
                str(ROOT/'firmware/abvm/tests/shift_identity_smoke.c'),
                '-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True)
        runtime=(ROOT/'firmware/abvm/pico/shift_identity_runtime.c').read_text()
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        self.assertIn('ShiftKind shift_kind_at_minute(uint16_t minute,uint16_t day_start,uint16_t day_end,',runtime)
        # The runtime's own answer and the board's answer must be one function of
        # one minute, or the two would disagree about the same wall clock.
        self.assertIn('return shift_kind_at_minute(s->minute,s->day_start,s->day_end,',runtime)
        self.assertIn('static FinishDecision finish_decision(uint32_t now) {',main)
        helper=main[main.index('static FinishDecision finish_decision(uint32_t now) {'):]
        helper=helper[:helper.index('static void service_cycle(uint32_t now) {')]
        # Both halves of the answer are already on the board: the wall anchor the
        # last bridge sample left behind, and the identity verified at the start
        # of the round that is ending.
        self.assertIn('wake_scheduler_wall_minute(&wake_scheduler,now,&decision.minute)',helper)
        self.assertIn('shift_identity.selected==decision.expected',helper)
        # No hotkey, no bridge reply, no timeout, and therefore no alarm risk.
        self.assertNotIn('shift_identity_begin',helper)
        self.assertNotIn('shift_launch_pending',helper)
        self.assertIn('if((vm.route_id!=1u&&vm.route_id!=3u)||shift_checkpoint||',main)
    def test_a_turned_over_window_switches_instead_of_sleeping_through_a_shift(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        block=re.search(r'else if\(action==CYCLE_ACTION_START_FINISH\) \{(.*?)\n    \}\n\}',main,re.S)
        self.assertIsNotNone(block)
        body=block.group(1)
        # The decision has to come first: the authored Finish closes the game and
        # sleeps the machine, and a sleeping machine cannot be switched.
        self.assertLess(body.index('finish_decision(now)'),
                        body.index('abvm_start_route(&vm,cycle_runtime_finish_route(),now)'))
        # The attempt is persisted before any authored restart step, so a reboot
        # in the middle of the switch route still counts as an attempt.
        self.assertLess(body.index('cycle_runtime_begin_shift(finish.route,(uint8_t)finish.expected,'),
                        body.index('abvm_start_route(&vm,finish.route,now)'))
        self.assertIn('shift_identity_forget(&shift_identity);',body)
        # The switch is named with the numbers an operator needs to read it back,
        # and the verified identity is read before the check is forgotten.
        self.assertIn('EVT|CYCLE|finish|action=switch|target=%s|route=%u|attempt=%u|minute=%u|verified=%s|origin=board',main)
        self.assertIn('EVT|CYCLE|finish|action=finish|reason=%s|minute=%u',main)
        self.assertLess(body.index('verified=%s|origin=board'),
                        body.index('shift_identity_forget(&shift_identity);'))
    def test_an_unanswerable_window_question_never_invents_a_switch(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        helper=main[main.index('static FinishDecision finish_decision(uint32_t now) {'):]
        helper=helper[:helper.index('static void service_cycle(uint32_t now) {')]
        # Route 0 is the only value that leaves the authored Finish in charge.
        self.assertIn('FinishDecision decision={0u,1440u,SHIFT_GLOBAL,"no-schedule"};',helper)
        route=helper.index('decision.route=decision.expected==SHIFT_DAY?')
        # A schedule that is not flashed, a board with no wall anchor, an
        # uncovered minute, an identity the round never verified, and a window
        # that has not turned over all end the same way: the authored Finish.
        for reason in ('if(!shift_identity.schedule_enabled)return decision;',
                       'if(!wake_scheduler_wall_minute(&wake_scheduler,now,&decision.minute))return decision;',
                       'if(decision.expected==SHIFT_GLOBAL){decision.reason="gap";return decision;}',
                       'if(shift_identity.selected==SHIFT_GLOBAL){decision.reason="identity-unknown";return decision;}',
                       'if(shift_identity.selected==decision.expected){decision.reason="window-same";return decision;}'):
            self.assertIn(reason,helper)
            self.assertLess(helper.index(reason),route)
        self.assertIn('decision.reason="window-turned";',helper)
    def test_a_finish_switch_is_bounded_and_loud(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        block=re.search(r'else if\(action==CYCLE_ACTION_START_FINISH\) \{(.*?)\n    \}\n\}',main,re.S)
        self.assertIsNotNone(block)
        body=block.group(1)
        self.assertIn('static void fail_shift_check(uint32_t now,const char *reason);',main)
        self.assertLess(main.index('static void fail_shift_check(uint32_t now,const char *reason);'),
                        main.index('static void service_cycle(uint32_t now) {'))
        # The same bound the bridge-driven switch obeys, and a loud stop instead
        # of a silent sleep when the owed switch cannot be taken.
        self.assertIn('if(calibration_store_shift_attempts()>=shift_identity.max_attempts) {',body)
        self.assertIn('fail_shift_check(now,"finish-switch-limit");return;',body)
        self.assertIn('fail_shift_check(now,"finish-switch-marker");return;',body)
        self.assertIn('ERR|CYCLE|finish|action=switch-blocked|reason=attempt-limit',main)
        self.assertIn('ERR|CYCLE|finish|action=switch-blocked|reason=marker-write',main)
        self.assertLess(body.index('fail_shift_check(now,"finish-switch-limit");return;'),
                        body.index('abvm_start_route(&vm,finish.route,now)'))
        self.assertLess(body.index('fail_shift_check(now,"finish-switch-marker");return;'),
                        body.index('abvm_start_route(&vm,finish.route,now)'))
    def test_a_clockless_board_asks_the_host_for_its_clock(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        challenge=main[main.index('else if (!strcmp(line,"SHIFT?")) {'):]
        challenge=challenge[:challenge.index('else if (!strncmp(line,"SHIFT!|",7)||!strncmp(line,"SHIFT2!|",8)) {')]
        # A board that lost power keeps the schedule it was flashed with and no
        # wall anchor, so the handshake is the only door left to a clock.  A check
        # in flight still owns the answer, and a board that already has a clock is
        # never offered a second one.
        self.assertLess(challenge.index('shift_checkpoint&&shift_identity.phase==SHIFT_WAIT_REPLY'),
                        challenge.index('wake_scheduler.enabled&&!wake_scheduler.synced'))
        self.assertIn('else if(wake_scheduler.enabled&&!wake_scheduler.synced) {',challenge)
        self.assertIn('printf("OK|SHIFT-CHALLENGE|%08lx|clock=1\\n"',challenge)
        self.assertIn('printf("ERR|SHIFT|not-waiting\\n");',challenge)
        self.assertIn('EVT|CLOCK|challenge|nonce=%08lx',challenge)
        reply=main[main.index('else if(valid&&with_clock&&clock_request_pending&&nonce==clock_request_nonce)'):]
        reply=reply[:reply.index('if(accepted) printf(')]
        # A tool that carries its own one-shot stamp sends `TIME!` first, so the
        # board may already keep time by the time this reply arrives: the stamp is
        # left alone rather than re-synced over a real deadline, and the reply is
        # still acknowledged.
        self.assertIn('OK|CLOCK|skipped|reason=synced|minute=%u',reply)
        self.assertLess(reply.index('if(wake_scheduler.synced)'),
                        reply.index('wake_scheduler_sync(&wake_scheduler,now,minute)'))
        # No check is in flight, so no identity verdict may be applied to this
        # reply: only the stamp is taken, and the acknowledgement is sent so the
        # host tool releases the port instead of waiting out its own timeout.
        self.assertIn('wake_scheduler_sync(&wake_scheduler,now,minute)',reply)
        self.assertIn('OK|CLOCK|stamp|minute=%u|window=%02u:%02u|in=%lu|lead=%u',reply)
        self.assertIn('OK|SHIFT-ACCEPTED|%08lx',reply)
        self.assertNotIn('shift_identity_reply_clock',reply)
        self.assertNotIn('shift_identity.selected',reply)
        self.assertIn('return;',reply)
    def test_the_clock_request_is_a_keystroke_that_needs_nobody_new(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        clock=main[main.index('static bool clock_request_allowed(void) {'):]
        clock=clock[:clock.index('static void service_wake(uint32_t now) {')]
        # The request is the authored shift step's own keys, so the host tool that
        # answers a check answers this too: nothing new is installed anywhere.
        self.assertIn('key.flags=shift_identity.key_count;',clock)
        self.assertIn('key.operand_c=shift_identity.hold_min;key.operand_d=shift_identity.hold_max;',clock)
        self.assertIn('key.operand_b|=(uint32_t)shift_identity.keys[i]<<(8u*i);',clock)
        self.assertIn('shift_identity.key_count>0u;',clock)
        # Only a board with no clock asks, and only while nothing else owns the
        # machine: a round in flight reaches the bridge through its own check.
        self.assertIn('wake_scheduler.enabled&&!wake_scheduler.synced&&',clock)
        self.assertIn('!clock_request_done&&wake_recovery_phase==WAKE_RECOVERY_IDLE&&',clock)
        self.assertIn('!shift_checkpoint&&!calibration_runtime_active()&&',clock)
        self.assertIn('vm.status!=ABVM_STATUS_RUNNING&&vm.status!=ABVM_STATUS_PAUSED&&',clock)
        # A press is a keystroke on a live desktop: the host has to be up first.
        self.assertIn('if(!wake_host_up()) { clock_request_waiting=false; return; }',clock)
        # Bounded, named, and it gives up loudly instead of asking forever.
        self.assertIn('CLOCK_REQUEST_SETTLE_MS',main)
        self.assertIn('CLOCK_REQUEST_RETRY_MS',main)
        self.assertIn('#define CLOCK_REQUEST_MAX_ATTEMPTS 3u',main)
        self.assertIn('EVT|CLOCK|request|state=waiting|settle-ms=%u|host=%s',clock)
        self.assertIn('EVT|CLOCK|request|attempt=%u|keys=%u|at-s=%u',clock)
        self.assertIn('ERR|CLOCK|no-host|attempts=%u|hint=TIME!',clock)
        self.assertIn('service_clock_request(now); service_boot_resume(now);',main)
    def test_a_session_that_survives_a_power_cut_is_decided_by_the_window(self):
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        window=main[main.index('static bool shift_window_open(uint16_t *minute_out,ShiftKind *kind_out) {'):]
        window=window[:window.index('static void service_boot_resume')]
        # Both halves of the answer are on the board already: the schedule it was
        # flashed with, and the wall anchor the clock sample just handed over.
        self.assertIn('wake_scheduler_wall_minute(&wake_scheduler,now_ms(),&minute)',window)
        self.assertIn('shift_kind_at_minute(minute,shift_identity.day_start,shift_identity.day_end,',window)
        self.assertIn('return kind!=SHIFT_GLOBAL;',window)
        boot=main[main.index('static void service_boot_resume(uint32_t now) {'):]
        boot=boot[:boot.index('static void service_wake(uint32_t now) {')]
        # A session found at boot is adopted inside a shift and dropped in the rest
        # window between two shifts, which is not work time.
        self.assertIn('if(cycle_runtime_session_pending()) {',boot)
        self.assertIn('cycle_runtime_session_drop();',boot)
        self.assertIn('EVT|CYCLE|resume|skipped|reason=rest-window|minute=%u',boot)
        self.assertIn('cycle_runtime_session_adopt(now)',boot)
        self.assertIn('EVT|CYCLE|resume|decision=adopted|minute=%u|window=%s',boot)
        self.assertLess(boot.index('cycle_runtime_session_drop();'),
                        boot.index('cycle_runtime_session_adopt(now)'))
        # No session survived, but the board lost power inside a shift: the machine
        # came up with nothing running and this shift is the one it must serve.
        # Only a clockless boot proves the cut, and an operator stop is a decision
        # about this session that no boot may undo.
        self.assertIn('board_booted_clockless&&clocked&&!operator_stopped_since_boot&&',boot)
        self.assertIn('EVT|CYCLE|start|reason=power-cut|minute=%u|window=%s',boot)
        self.assertIn('board_booted_clockless=wake_scheduler.enabled&&!wake_scheduler.synced;',main)
        stop=main[main.index('static void stop_control(uint32_t now) {'):]
        stop=stop[:stop.index('printf("OK|GUARD|OFF\\n")')]
        self.assertIn('operator_stopped_since_boot=true;',stop)
    def test_the_round_itself_is_the_marker_a_boot_reads(self):
        runtime=(ROOT/'firmware/abvm/pico/cycle_runtime.c').read_text()
        store=(ROOT/'firmware/abvm/pico/calibration_store.c').read_text()
        header=(ROOT/'firmware/abvm/pico/calibration_store.h').read_text()
        # The marker used to live only across the restart, so a power cut in the
        # middle of a round left the next boot with nothing to recognise.  It now
        # outlives the round it belongs to and is cleared by every terminal path.
        self.assertIn('bool calibration_store_cycle_mark_live(void);',header)
        self.assertIn('bool calibration_store_cycle_mark_live(void){',store)
        start=runtime[runtime.index('bool cycle_runtime_manual_start(uint32_t now) {'):]
        start=start[:start.index('void cycle_runtime_manual_stop(void) {')]
        self.assertIn('calibration_store_cycle_mark_live()',start)
        complete=runtime[runtime.index('bool cycle_runtime_route_complete(uint16_t route_id,uint32_t now) {'):]
        complete=complete[:complete.index('void cycle_runtime_fail(uint8_t stage) {')]
        self.assertIn('if(!calibration_store_cycle_mark_live()){',complete)
        self.assertNotIn('calibration_store_cycle_clear_armed()',complete)
        # A session found at boot waits outside every phase: whether the shift it
        # belongs to is still open is a question only a wall clock can answer, and
        # nothing may be blocked while the board waits for one.
        boot=runtime[runtime.index('bool cycle_runtime_init(const AbvmVm *vm,uint32_t now) {'):]
        boot=boot[:boot.index('bool cycle_runtime_available(void)')]
        self.assertIn('cycle.resume_pending=true;',boot)
        self.assertNotIn('cycle.phase=CYCLE_WAIT_USB;cycle.shift_wait_since=now;',boot)
        self.assertIn('emit(CYCLE_EVENT_ARMED_AT_BOOT);',boot)
        adopt=runtime[runtime.index('bool cycle_runtime_session_adopt(uint32_t now) {'):]
        adopt=adopt[:adopt.index('void cycle_runtime_session_drop(void) {')]
        self.assertIn('calibration_store_cycle_mark_live()',adopt)
        self.assertIn('cycle.phase=CYCLE_WAIT_USB;cycle.shift_wait_since=now;',adopt)
        self.assertIn('cycle.down_seen=true;',adopt)
        drop=runtime[runtime.index('void cycle_runtime_session_drop(void) {'):]
        drop=drop[:drop.index('bool cycle_runtime_waiting_for_usb(void)')]
        self.assertIn('(void)calibration_store_cycle_reset();',drop)
    def test_a_round_a_power_cut_interrupted_is_not_owed_the_rest(self):
        # The settle the Startup route opens with is the rest between rounds.  A
        # session that survived a power cut never finished its round, so the board
        # enters that route without the delay it opens with -- and only that delay:
        # every authored step after it still runs.  A restart the board drove is
        # untouched and still waits the rest out in full.
        main=(ROOT/'firmware/abvm/pico/main.c').read_text()
        startup=main[main.index('} else if(action==CYCLE_ACTION_START_STARTUP) {'):]
        startup=startup[:startup.index('} else if(action==CYCLE_ACTION_SHIFT_STALLED) {')]
        self.assertIn('bool skip_settle=cycle_runtime_skip_startup_settle();',startup)
        self.assertIn('abvm_start_route_without_opening_delay(&vm,cycle_runtime_startup_route(),now)',startup)
        self.assertIn('abvm_start_route(&vm,cycle_runtime_startup_route(),now)',startup)
        self.assertIn('EVT|CYCLE|startup|settle=skipped|reason=power-cut',startup)
        # The stalled-switch path replays Startup after a reboot this board drove,
        # so it keeps the rest: it must never reach for the settle-free entry.
        stalled=main[main.index('} else if(action==CYCLE_ACTION_SHIFT_STALLED) {'):]
        stalled=stalled[:stalled.index('} else if(action==CYCLE_ACTION_START_FINISH) {')]
        self.assertNotIn('without_opening_delay',stalled)
        runtime=(ROOT/'firmware/abvm/pico/cycle_runtime.c').read_text()
        adopt=runtime[runtime.index('bool cycle_runtime_session_adopt(uint32_t now) {'):]
        adopt=adopt[:adopt.index('void cycle_runtime_session_drop(void) {')]
        self.assertIn('cycle.skip_settle=true;',adopt)
        drop=runtime[runtime.index('void cycle_runtime_session_drop(void) {'):]
        drop=drop[:drop.index('bool cycle_runtime_skip_startup_settle(void)')]
        self.assertIn('cycle.skip_settle=false;',drop)
        after=runtime[runtime.index('bool cycle_runtime_begin_after(uint32_t now) {'):]
        after=after[:after.index('void cycle_runtime_begin_startup(void) {')]
        self.assertIn('cycle.skip_settle=false;',after)
        shift=runtime[runtime.index('bool cycle_runtime_begin_shift('):]
        shift=shift[:shift.index('bool cycle_runtime_shift_confirmed')]
        self.assertIn('cycle.skip_settle=false;',shift)
        start=runtime[runtime.index('void cycle_runtime_begin_startup(void) {'):]
        start=start[:start.index('void cycle_runtime_begin_finish')]
        self.assertIn('cycle.skip_settle=false;',start)
        # The VM leaves out a delay and nothing else, and never the END that closes
        # a route with no steps left to run.
        vm=(ROOT/'firmware/abvm/src/abvm_vm.c').read_text()
        skip=vm[vm.index('int abvm_start_route_without_opening_delay('):]
        skip=skip[:skip.index('\n}\n')]
        self.assertIn('opening.opcode == ABVM_OP_DELAY',skip)
        self.assertIn('route.length > 1u',skip)
        self.assertIn('vm->lanes[0].pc++;',skip)
        self.assertIn('vm->lanes[0].due = now;',skip)
        # A route that opens with an action is entered exactly where it always was.
        self.assertIn('return enter_route(vm, &route, now);',vm)
if __name__=='__main__':unittest.main()
