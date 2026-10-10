
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "bsp/board.h"
#include "pico/stdlib.h"
#include "hardware/pwm.h"
#include "tusb.h"
#include "abvm_vm.h"
#include "arm_uart_mouse.h"
#include "hid_keyboard.h"
#include "light_sensor.h"
#include "guard_runtime.h"
#include "calibration_runtime.h"
#include "calibration_store.h"
#include "buzzer.h"
#include "cycle_runtime.h"
#include "game_buff_runtime.h"
#include "shift_identity_runtime.h"
#include "wake_scheduler.h"

extern const uint8_t *abvm_program_data(void);
extern size_t abvm_program_size(void);
#define BUTTON_PAUSE_PIN 3u
#define BUTTON_START_STOP_PIN 4u
#define BUTTON_DEBOUNCE_MS 30u
#define BUTTON_LONG_MS 3000u
#define GAME_ROUTE_ID 8u
#define WHISPER_ROUTE_ID 10u
#define WHISPER_REPEAT_ROUTE_ID 12u
/* The board's own status LED: dim and steady while the board runs, bright only
 * for a power-up or a real power-button press.  The design note sits with the
 * LED block further down; the numbers live here because the console command that
 * sets the level is parsed before that block. */
#define STATUS_LED_PIN 25u
#define STATUS_LED_PWM_WRAP 999u        /* ~1 kHz at the default 125 MHz clock */
#define STATUS_LED_DIM_LEVEL 20u        /* 2% duty: visible, not a beacon */
#define STATUS_LED_BOOT_MS 600u
#define STATUS_LED_PRESS_MS 600u
/* Autonomous shift wake.  The Pico owns no RTC, so the bridge clock sample taken
 * at each shift identity check is converted into a monotonic deadline.  A window
 * start is therefore woken this many minutes early, and the machine is given a
 * settle window before the authored Startup route starts driving the desktop. */
#define WAKE_LEAD_MINUTES 2u
#define WAKE_PULSE_COMMAND "MMOVE|1,0,rel,2"
#define WAKE_PULSE_TIMEOUT_MS 5000u
#define WAKE_RESUME_TIMEOUT_MS 60000u
#define WAKE_SETTLE_MS 15000u
/* The bridge re-announces its host-USB verdict every two seconds, so a single
 * `UP` report can be that old.  The machine can also be a second away from
 * suspending when the deadline lands, and reading that as "the host is already
 * awake" silently eats the only wake of the window.  The shortcut is therefore
 * taken only after the host has been reported awake for this long. */
#define WAKE_HOST_AWAKE_GRACE_MS 5000u
#define WAKE_MAX_ATTEMPTS 3u
#define WAKE_RETRY_MS 300000u
/* A verified wake leaves the host on the Windows lock screen, and the authored
 * macro language has no click opcode at all (ABVM knows motion, keys and typing
 * only), so nothing in the project can dismiss it.  Paced Enters clear the lock
 * screen and sign the machine in without touching the pointer, which puts the
 * authored round on the desktop instead of on a lock screen.  It only ever runs
 * on a host this board actually woke.
 *
 * The phase is bounded on purpose.  A key report is only delivered while the
 * host keeps this board's own port resumed, and the host can resume through the
 * Arduino board instead, which leaves our endpoint unable to flush.  Waiting for
 * that endpoint forever used to park the whole wake machine in this phase, so a
 * later arm could never reach its pulse and the shift was lost.  Now the phase
 * gives up after WAKE_DISMISS_TIMEOUT_MS and starts the round anyway, and while
 * our own bus is suspended the board that certainly resumed presses the key. */
#define WAKE_DISMISS_ENABLED 1
#define WAKE_DISMISS_KEY 13u /* VK_RETURN */
#define WAKE_DISMISS_PRESSES 3u
#define WAKE_DISMISS_GAP_MS 1500u
#define WAKE_DISMISS_TIMEOUT_MS 20000u
#define WAKE_DISMISS_ARM_FALLBACK 1
#define WAKE_DISMISS_ARM_COMMAND "KCOMBO|13,40,90"
/* Two independent wake sources sit on the host bus: the Pico is itself a HID
 * keyboard whose descriptor advertises remote wake-up, and the Arduino board
 * raises RMWKUP from its own suspended mouse interface.  Both are armed by the
 * host independently, so both are used in the same pulse; the tunables exist so
 * a hardware bring-up can isolate one of them. */
#define WAKE_USE_PICO_WAKEUP 1
#define WAKE_USE_ARM_PULSE 1
/* A board reset loses the RAM deadline, and a power cut loses the wall anchor
 * with it.  Two boot states need the same medicine: the persisted record says the
 * host was asleep with a wake still owed, or the board came back with a schedule
 * and no clock at all.  In both cases one pulse brings the host back, the bridge
 * re-sends the sample, and the real deadline is re-armed.  The attempt counter
 * bounds both so a brownout loop can never turn into a wake storm. */
#define WAKE_RECOVERY_MAX_ATTEMPTS 3u
#define WAKE_RECOVERY_TIMEOUT_MS 120000u
/* How long a recovery boot waits for the first host-USB sample before it stops
 * asking and pulses anyway: the Arduino board re-announces its state every two
 * seconds, so a silent link is itself the emergency. */
#define WAKE_HOST_SAMPLE_TIMEOUT_MS 5000u
/* How long a recovery boot waits for USB enumeration before falling through to
 * the recovery pulse: an awake host mounts in milliseconds, a sleeping one never
 * does until the pulse resumes it. */
#define WAKE_MOUNT_TIMEOUT_MS 5000u
/* The persisted record is written only when the recovery decision changes, and
 * never more often than this, so a flapping USB state cannot burn the slot. */
#define WAKE_STORE_MIN_INTERVAL_MS 30000u
/* A machine that is fully off cannot be woken over USB: remote wake-up only
 * resumes a bus the host suspended, so neither the Pico's own resume nor the
 * Arduino board's RMWKUP has anything to drive from S5.  The one line that still
 * reaches a PC in soft-off is its own power button, which the ATX standby rail
 * keeps alive whenever the PSU has mains.  An optocoupler across the front-panel
 * header turns that button into a floating contact the board can close, and
 * because it is an optocoupler the two grounds stay separate -- the PC's ground
 * must never meet this board's.
 *
 * The press has to be momentary: holding the button for four seconds is a forced
 * power-off.  The line is therefore bounded on both sides and always released.
 * The board only presses it when nothing is on its own USB at all -- a host that
 * is merely suspended keeps the port mounted and is woken over the bus instead --
 * so a machine that is already running only sees a press if its own USB cable is
 * out.  Set Windows to ignore the power button ("Do nothing") before wiring this
 * line: a stray press is then a no-op, while a real one still boots a machine
 * that is off. */
#define POWER_BUTTON_ENABLED 1
#define POWER_BUTTON_PIN 7u
#define POWER_BUTTON_MS 300u
#define POWER_BUTTON_MIN_MS 100u
#define POWER_BUTTON_MAX_MS 1500u
/* A host that is still in POST is indistinguishable from a host that is off:
 * until its own USB stack comes up, nothing of ours is on its bus at all.  A
 * board that powered up together with the machine -- the power cut that restarted
 * both -- would therefore read "off" a few seconds in and press a power button
 * into a running POST, and a machine answers that by shutting down again, undoing
 * the very BIOS setting that brought it back.
 *
 * The press is held back for a grace instead, and the grace is this machine's own
 * cold start rather than a vendor's: the longest time this board has watched the
 * host take to put its USB up, plus a margin, persisted in the wake record so it
 * survives the same power cut that needs it.  Nothing here is brand-specific --
 * what is being waited out is the machine's POST, whatever made it. */
#define POWER_BUTTON_GRACE_DEFAULT_MS 60000u
#define POWER_BUTTON_GRACE_MIN_MS 30000u
#define POWER_BUTTON_GRACE_MAX_MS 150000u
#define POWER_BUTTON_GRACE_MARGIN_MS 10000u
/* A sample outside this range cannot have come from a POST on this class of
 * machine, so it is treated as no sample at all.  The floor sits well above the
 * few seconds this board needs to enumerate itself: a board that restarted while
 * the host was already up sees that host on its bus almost at once, and a cold
 * start read off that start would be this board's own enumeration time.  A grace
 * that short is precisely the press into a running POST this feature exists to
 * prevent, so such a sample is not a sample.  A machine that really is that quick
 * simply keeps the default wait. */
#define HOST_BOOT_LEARN_MIN_S 8u
#define HOST_BOOT_LEARN_MAX_S 120u

typedef struct Button { uint pin; bool raw, stable, long_sent, consumed; uint32_t changed_at, pressed_at; } Button;
typedef enum ButtonEvent { BUTTON_NONE, BUTTON_DOWN, BUTTON_SHORT, BUTTON_LONG } ButtonEvent;
static AbvmVm vm;
static GameBuffRuntime game_buffs;
static bool buff_checkpoint, buff_key_inflight;
static uint8_t buff_lane;
static uint32_t game_fishing_elapsed, game_age_at, buff_status_at, buff_generation;
#define BUFF_KEY_LANE 253u
#define SHIFT_KEY_LANE 252u
static ShiftIdentityRuntime shift_identity;
static bool shift_checkpoint,shift_key_inflight,shift_launch_pending,shift_cue_started,shift_failure_paused;
static uint8_t shift_lane;
static uint32_t shift_generation,shift_cue_until;

typedef enum WakePhase { WAKE_PHASE_IDLE=0, WAKE_PHASE_PULSE, WAKE_PHASE_RESUME, WAKE_PHASE_SETTLE, WAKE_PHASE_DISMISS } WakePhase;
typedef enum WakeRecoveryPhase { WAKE_RECOVERY_IDLE=0, WAKE_RECOVERY_PULSE, WAKE_RECOVERY_WAIT } WakeRecoveryPhase;
/* Why a recovery boot is running.  An owed wake is recovered from the persisted
 * decision; a clockless power-on has no persisted deadline to point at, yet the
 * same pulse is the only route back to a wall clock.  Both spend one budget, and
 * the reason is logged so an operator can tell the two apart. */
typedef enum WakeRecoveryReason { WAKE_RECOVERY_REASON_NONE=0, WAKE_RECOVERY_REASON_OWED, WAKE_RECOVERY_REASON_CLOCK } WakeRecoveryReason;
static WakeScheduler wake_scheduler;
static WakePhase wake_phase;
static WakeRecoveryPhase wake_recovery_phase;
static WakeRecoveryReason wake_recovery_reason;
static bool wake_pulse_inflight;
static bool wake_woke_host,wake_dismiss_done;
static uint8_t wake_dismiss_step;
static uint8_t wake_attempts;
static uint32_t wake_deadline,wake_retry_at,wake_recovery_deadline,wake_dismiss_started;
static bool wake_pulse_wait_logged;
static uint32_t wake_host_awake_at;
static bool wake_due_logged,wake_block_logged;
static bool pico_usb_suspended,pico_remote_wakeup_en;
static WakeStoreState wake_store_last;
static uint32_t wake_store_next_at;
static bool wake_store_dirty;
static bool power_button_held;
static uint32_t power_button_release_at;
static uint8_t power_button_presses;
/* The grace is measured from this board's own boot, because that is the only
 * moment at which "nothing on our bus" can mean "the host is booting" as well as
 * "the host is off".  The origin moves to the press once one is sent: after that
 * the host's cold start began with the press, not with this board. */
static uint32_t power_button_boot_at;
static uint32_t host_boot_origin_at;
static uint16_t host_boot_learned_s;
static bool host_boot_measured,power_button_grace_logged;
static void wake_attempts_reset(void);
static void wake_store_service(uint32_t now,bool force);
static void print_wake_state(void);
static void wake_report_blocked(const char *reason,uint32_t now);
static const char *wake_recovery_reason_text(void);
static bool power_button_press(uint32_t now,uint16_t hold_ms);
static uint16_t host_boot_learned_value(void);
static uint32_t power_button_grace_ms(void);
static void led_dim(void);
static void led_flash(uint32_t now,uint32_t hold_ms);
static void led_set_dim(uint16_t level);


static Button pause_button = {.pin=BUTTON_PAUSE_PIN};
static Button start_button = {.pin=BUTTON_START_STOP_PIN};
static char command[256];
static size_t command_length;
static bool arm_fault_reported;
static bool ui_sound_calibration_pending;
static uint32_t ui_sound_calibration_deadline;
static bool ambient_mouse_available,ambient_mouse_inflight;
static uint16_t ambient_mouse_constant;
static uint8_t ambient_mouse_environment_mask;
static uint32_t ambient_mouse_interval_min,ambient_mouse_interval_max;
static uint32_t ambient_mouse_next_due,ambient_mouse_rng;
static bool buzzer_action_pending;
static uint8_t buzzer_action_lane;
static uint32_t buzzer_action_deadline;
static bool ui_sound_watch_pending;
static bool ui_sound_watch_armed;
static bool ui_sound_trigger_pending;
static uint8_t ui_sound_trigger_button;
static uint16_t ui_sound_trigger_hold_min,ui_sound_trigger_hold_max;
static uint32_t ui_sound_trigger_due;
static uint16_t ui_sound_trigger_peak;
static bool ui_light_watch_pending;
static bool ui_light_watch_armed;
static bool ui_light_trigger_pending;
static uint8_t ui_light_trigger_key;
static uint16_t ui_light_trigger_hold_min,ui_light_trigger_hold_max;
static uint32_t ui_light_trigger_due,ui_light_trigger_lux;
static bool ui_buzzer_reply_pending;
static bool ui_buzzer_sequence_reply;
static uint32_t ui_buzzer_reply_deadline;
typedef struct GlobalWhisperProfile {
    bool enabled;
    uint16_t id,route_id,threshold,maximum,minimum;
    uint32_t cooldown_ms,next_allowed;
} GlobalWhisperProfile;
static GlobalWhisperProfile whisper_profiles[2];
static bool global_sound_enabled;
static uint16_t global_sound_threshold,global_sound_minimum;
static bool pending_sound_whisper;
static uint16_t pending_sound_profile,pending_sound_listener,pending_sound_peak;
static uint32_t now_ms(void) { return to_ms_since_boot(get_absolute_time()); }
static bool parse_csv_u32(const char *text,uint32_t *values,size_t count) {
    if(!text||!values||!count)return false;
    for(size_t i=0;i<count;++i){
        char *end=NULL;values[i]=strtoul(text,&end,10);
        if(!end||end==text)return false;
        if(i+1u<count){if(*end!=',')return false;text=end+1;}
        else if(*end)return false;
    }
    return true;
}
static uint16_t local_u16(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}
static uint32_t local_u32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static void load_whisper_profile(void) {
    memset(whisper_profiles,0,sizeof(whisper_profiles));
    global_sound_enabled=false;
    global_sound_threshold=global_sound_minimum=0u;
    uint32_t cursor=vm.header.constant_offset;
    uint32_t end=cursor+vm.header.constant_size;
    while(cursor<end) {
        if(cursor+8u>end)return;
        uint8_t kind=vm.image[cursor];
        uint32_t size=local_u32(vm.image+cursor+4u);
        cursor+=8u;
        if(cursor+size>end)return;
        const uint8_t *payload=vm.image+cursor;
        if(kind==ABVM_CONST_SOUND&&size==8u) {
            uint16_t id=local_u16(payload);
            uint32_t packed=local_u32(payload+4u);
            uint16_t detector=local_u16(payload+2u);
            uint16_t detector_minimum=(uint16_t)(packed&0xffffu);
            if(!detector)
                (void)calibration_store_sound_get(
                    id,&detector,&detector_minimum);
            if(detector&&detector_minimum&&
               (!global_sound_threshold||detector<global_sound_threshold))
                global_sound_threshold=detector;
            if(detector_minimum&&
               (!global_sound_minimum||detector_minimum<global_sound_minimum))
                global_sound_minimum=detector_minimum;
            uint16_t maximum=(uint16_t)((packed>>16)&0x3ffu);
            uint32_t cooldown_ms=((packed>>26)&0x3fu)*1000u;
            GlobalWhisperProfile *target=id==1u?&whisper_profiles[0]:
                                           id==3u?&whisper_profiles[1]:NULL;
            /* Ordinary scoped WATCH descriptors leave the high word zero. */
            if(target&&maximum) {
                target->id=id;
                target->route_id=id==3u?WHISPER_REPEAT_ROUTE_ID:WHISPER_ROUTE_ID;
                target->threshold=local_u16(payload+2u);
                target->minimum=(uint16_t)(packed&0xffffu);
                target->maximum=maximum;
                target->cooldown_ms=cooldown_ms;
                if(!target->threshold)
                    (void)calibration_store_sound_get(
                        id,&target->threshold,&target->minimum);
                target->enabled=target->threshold>0u &&
                    target->threshold<=target->maximum &&
                    target->maximum<=1023u&&target->minimum>0u;
            }
        }
        cursor=(cursor+size+3u)&~3u;
    }
    global_sound_enabled=(whisper_profiles[0].enabled||
                          whisper_profiles[1].enabled)&&
                         global_sound_threshold&&global_sound_minimum;
}
static uint16_t active_sound_watch_profile(void) {
    for (uint8_t i=0;i<ABVM_MAX_LANES;++i) {
        const AbvmLane *lane=&vm.lanes[i];
        if (lane->active && lane->blocked==ABVM_BLOCK_WATCH &&
            lane->watch_kind==ABVM_CONST_SOUND)
            return lane->watch_profile;
    }
    return 0u;
}
static bool whisper_interrupt_allowed(void) {
    return vm.status==ABVM_STATUS_RUNNING &&
           vm.route_id!=WHISPER_ROUTE_ID &&
           vm.route_id!=WHISPER_REPEAT_ROUTE_ID &&
           !cycle_runtime_restart_critical();
}
static bool input_lock_active(void) {
    return shift_checkpoint||hid_keyboard_locked()||arm_uart_mouse_busy();
}
static bool ambient_mouse_route_allowed(uint8_t profile) {
    /* Ambient is the non-Game sidecar.  It may run while a foreground route
     * waits; service_vm() yields until the internal movement completes so a
     * route mouse action can never race it on the single ARM link. */
    return profile>=1u&&profile<=4u;
}
static uint32_t ambient_random_next(void) {
    uint32_t x=ambient_mouse_rng?ambient_mouse_rng:0x7f4a7c15u;
    x^=x<<13;x^=x>>17;x^=x<<5;ambient_mouse_rng=x;return x;
}
static void ambient_mouse_arm_next(uint32_t now) {
    uint32_t span=ambient_mouse_interval_max-ambient_mouse_interval_min;
    ambient_mouse_next_due=now+ambient_mouse_interval_min+
        (span?ambient_random_next()%(span+1u):0u);
}
static void ambient_mouse_init(uint32_t now) {
    ambient_mouse_available=arm_uart_mouse_ambient_config(
        &vm,&ambient_mouse_constant,&ambient_mouse_environment_mask,
        &ambient_mouse_interval_min,&ambient_mouse_interval_max);
    ambient_mouse_inflight=false;
    ambient_mouse_rng=now^local_u32(vm.header.program_sha256);
    if(ambient_mouse_available)ambient_mouse_arm_next(now);
}
static void ambient_mouse_complete(uint32_t now) {
    ambient_mouse_inflight=false;
    if(ambient_mouse_available)ambient_mouse_arm_next(now);
}
static void service_ambient_mouse(uint32_t now) {
    if(shift_checkpoint)return;
    if(!ambient_mouse_available)return;
    if(ambient_mouse_inflight) {
        uint8_t profile=guard_runtime_active_profile();
        if(!guard_runtime_running()||guard_runtime_paused()||
           guard_runtime_watchdog_tripped()||
           cycle_runtime_restart_critical()||calibration_runtime_active()||
           light_sensor_calibration_active()||
           !ambient_mouse_route_allowed(profile)||
           !(ambient_mouse_environment_mask&(uint8_t)(1u<<(profile-1u))))
            arm_uart_mouse_release_all(now);
        return;
    }
    if(!guard_runtime_running()||guard_runtime_paused()||
       guard_runtime_watchdog_tripped()||cycle_runtime_restart_critical()||
       calibration_runtime_active()||light_sensor_calibration_active()||
       vm.status==ABVM_STATUS_PAUSED||
       input_lock_active()||
       (int32_t)(now-ambient_mouse_next_due)<0)
        return;
    uint8_t profile=guard_runtime_active_profile();
    if(!ambient_mouse_route_allowed(profile)||
       !(ambient_mouse_environment_mask&(uint8_t)(1u<<(profile-1u)))) {
        ambient_mouse_arm_next(now);
        return;
    }
    ArmMouseSubmit result=arm_uart_mouse_submit_ambient(
        &vm,ambient_mouse_constant,now);
    if(result==ARM_MOUSE_ACCEPTED) {
        ambient_mouse_inflight=true;
        printf("ARM|ambient|accepted|profile=%s\n",
               guard_runtime_profile_name(profile));
    } else if(result==ARM_MOUSE_INVALID) {
        ambient_mouse_available=false;
        printf("ERR|ARM|ambient|invalid\n");
    }
}
static GlobalWhisperProfile *global_whisper_by_id(uint16_t id) {
    for(uint8_t i=0;i<2u;++i)
        if(whisper_profiles[i].id==id)return &whisper_profiles[i];
    return NULL;
}
static bool start_sound_whisper(GlobalWhisperProfile *selected,
                                uint16_t listener,uint16_t peak,
                                uint32_t now) {
    if(!selected||!whisper_interrupt_allowed())return false;
    if(selected->next_allowed&&(int32_t)(now-selected->next_allowed)<0) {
        printf("MISS|WHISPER|reason=cooldown|profile=%u|remaining=%lu\n",
               selected->id,(unsigned long)(selected->next_allowed-now));
        return true;
    }
    if(!abvm_interrupt_route(&vm,selected->route_id,now))return false;
    selected->next_allowed=selected->cooldown_ms?
        now+selected->cooldown_ms:0u;
    buzzer_play(selected->id==3u?
        BUZZER_CUE_WHISPER_REPEAT:BUZZER_CUE_WHISPER,now);
    printf("CONTROL|interrupt|route=%s|source=sound|listener=%u|profile=%u|peak=%u|range=%u-%u\n",
           selected->id==3u?"WhisperRepeat":"Whisper",
           listener,selected->id,peak,selected->threshold,selected->maximum);
    return true;
}
static void service_pending_sound_whisper(uint32_t now) {
    if(!pending_sound_whisper)return;
    if(cycle_runtime_restart_critical()) {
        pending_sound_whisper=false;
        return;
    }
    if(input_lock_active())return;
    GlobalWhisperProfile *selected=
        global_whisper_by_id(pending_sound_profile);
    if(start_sound_whisper(selected,pending_sound_listener,
                           pending_sound_peak,now))
        pending_sound_whisper=false;
}
static void service_global_sound_listener(uint32_t now) {
    if(!global_sound_enabled||pending_sound_whisper||
       cycle_runtime_restart_critical()||
       calibration_runtime_active()||ui_sound_watch_pending||
       vm.status!=ABVM_STATUS_RUNNING||
       vm.route_id==WHISPER_ROUTE_ID||
       vm.route_id==WHISPER_REPEAT_ROUTE_ID||
       arm_uart_sound_active()||arm_uart_mouse_releasing()||
       !arm_uart_mouse_ready())return;
    (void)arm_uart_sound_restart(now,1u,global_sound_threshold,
                                 global_sound_minimum,30000u);
}
/* While the host is asleep the CDC is disconnected and every log line would be
 * thrown away, which is exactly the window an operator has to read afterwards:
 * the wake decision, the pulse and its result all happen with nobody listening.
 * The tail of the output is therefore kept in RAM and replayed, marked RPL|, the
 * moment the host is back.  The buffer is bounded and only whole writes are
 * replayed, so it can never grow or block. */
#define LOG_REPLAY_BYTES 2048
static char log_replay[LOG_REPLAY_BYTES];
static size_t log_replay_used;

static void log_replay_add(const char *text, size_t length) {
    if (!length) return;
    if (length >= LOG_REPLAY_BYTES) {
        memcpy(log_replay, text + (length - LOG_REPLAY_BYTES), LOG_REPLAY_BYTES);
        log_replay_used = LOG_REPLAY_BYTES;
        return;
    }
    if (log_replay_used + length > LOG_REPLAY_BYTES) {
        size_t drop = log_replay_used + length - LOG_REPLAY_BYTES;
        memmove(log_replay, log_replay + drop, log_replay_used - drop);
        log_replay_used -= drop;
    }
    memcpy(log_replay + log_replay_used, text, length);
    log_replay_used += length;
}

static size_t cdc_write_all(const char *text, size_t length) {
    size_t sent = 0u;
    while (sent < length) {
        uint32_t chunk = tud_cdc_write(text + sent, (uint32_t)(length - sent));
        if (!chunk) break;
        sent += chunk;
    }
    tud_cdc_write_flush();
    return sent;
}

static int cdc_printf(const char *format, ...) {
    char output[384]; va_list args; va_start(args, format);
    int length = vsnprintf(output, sizeof(output), format, args); va_end(args);
    if (length <= 0) return length;
    size_t count = (size_t)length;
    if (count >= sizeof(output)) count = sizeof(output) - 1u;
    if (!tud_cdc_connected()) { log_replay_add(output, count); return length; }
    if (log_replay_used) {
        /* Chronological order: the held-back lines come first.  A host that is
         * not draining yet keeps the remainder for the next call. */
        static const char opening[] = "RPL|begin\n";
        static const char closing[] = "RPL|end\n";
        cdc_write_all(opening, sizeof(opening) - 1u);
        size_t sent = cdc_write_all(log_replay, log_replay_used);
        if (sent >= log_replay_used) log_replay_used = 0u;
        else {
            memmove(log_replay, log_replay + sent, log_replay_used - sent);
            log_replay_used -= sent;
        }
        cdc_write_all(closing, sizeof(closing) - 1u);
    }
    (void)cdc_write_all(output, count);
    return length;
}
#define printf cdc_printf
static void print_status(void) {
    WakeStoreState wake_state;
    uint16_t wall = 0u;
    bool wall_known = wake_scheduler_wall_minute(&wake_scheduler, now_ms(), &wall);
    if (!calibration_store_wake_get(&wake_state)) memset(&wake_state, 0, sizeof(wake_state));
    printf("STATUS|state=%s|route=%u|lanes=%u|pc0=%lu|pc1=%lu|frames0=%u|frames1=%u|suspended=%u|hid-busy=%u|sound-active=%u|light-present=%u|light-watch=%u|light-cal=%u|guard=%u|guard-paused=%u|guard-profile=%s|guard-stage=%u|cycle=%u|time=%lu|pico-usb=%u|pico-rw=%u|wake=%u|wake-target=%02u:%02u|wake-synced=%u|wake-wall=%02u:%02u|wake-recovery=%u|wake-host=%u|wake-phase=%u\n", abvm_status_name(vm.status), vm.route_id, vm.lane_count, (unsigned long)vm.lanes[0].pc, (unsigned long)vm.lanes[1].pc, vm.lanes[0].frame_count, vm.lanes[1].frame_count, vm.suspended.valid, hid_keyboard_busy() || arm_uart_mouse_busy(), arm_uart_sound_active(), light_sensor_present(), light_sensor_watch_active(), light_sensor_calibration_active(), guard_runtime_running(), guard_runtime_paused(), guard_runtime_profile_name(guard_runtime_active_profile()), guard_runtime_stage(), cycle_runtime_available(), (unsigned long)vm.now, pico_usb_suspended ? 1u : 0u, pico_remote_wakeup_en ? 1u : 0u, wake_scheduler_armed(&wake_scheduler) ? 1u : 0u, wake_scheduler.next_start / 60u, wake_scheduler.next_start % 60u, wall_known ? 1u : 0u, wall / 60u, wall % 60u, (unsigned)wake_state.recovery_attempts, wake_state.host_asleep ? 1u : 0u, (unsigned)wake_phase);
}
static void print_light_calibration_dump(void) {
    uint32_t low[9],high[9];uint16_t mask=0u;
    for(uint8_t id=1u;id<=9u;++id)
        if(calibration_store_light_get(id,&low[id-1u],&high[id-1u]))
            mask|=(uint16_t)(1u<<(id-1u));
    char profiles[256];size_t used=0u;
    if(!mask)snprintf(profiles,sizeof(profiles),"none");
    for(uint8_t id=1u;id<=9u;++id)if(mask&(1u<<(id-1u))){
        int written=snprintf(profiles+used,sizeof(profiles)-used,
                             "%s%u:%lu:%lu",used?",":"",id,
                             (unsigned long)low[id-1u],
                             (unsigned long)high[id-1u]);
        if(written<0||(size_t)written>=sizeof(profiles)-used){
            printf("ERR|INTERNAL|CALDUMP\n");return;
        }
        used+=(size_t)written;
    }
    /*
     * Keep the whole reply in one TinyUSB write.  Several immediate printf
     * calls can fill the CDC endpoint and drop the final newline, leaving the
     * PC bridge waiting forever for a complete response line.
     */
    printf("OK|CALDUMP|LIGHT|revision=%lu|mask=%04x|profiles=%s\n",
           (unsigned long)calibration_store_revision(),mask,profiles);
}
static void release_all_actors(uint32_t now) {
    if(shift_checkpoint && shift_identity.phase!=SHIFT_FAILED) {
        shift_identity_cancel(&shift_identity);shift_failure_paused=true;
    }
    if(shift_key_inflight) {
        shift_key_inflight=false;hid_keyboard_release_all();hid_keyboard_discard_completion();
    }
    if (buff_key_inflight) {
        game_buff_key_cancelled(&game_buffs);
        buff_key_inflight=false;
        hid_keyboard_release_all();
        hid_keyboard_discard_completion();
    } else {
        /* An optical route may interrupt eating after key release. Retry that
         * unfinished buff, not already-completed entries in the shuffled batch. */
        if (buff_checkpoint && vm.route_id!=GAME_ROUTE_ID &&
            game_buffs.phase==GAME_BUFF_AFTER) game_buffs.phase=GAME_BUFF_REQUEST;
        hid_keyboard_release_all();
    }
 arm_uart_mouse_release_all(now);
    light_sensor_cancel_watch(now);
    /* RELEASE_ALL is primarily an input/watch safety boundary.  Do not cut
     * short Guard/calibration feedback that was started immediately before
     * the VM emits its route-entry release.  Only a project/direct BEEP owns
     * an action that must be cancelled at this boundary. */
    if (buzzer_action_pending || ui_buzzer_reply_pending) buzzer_silence();
    buzzer_action_pending=false;ui_sound_watch_pending=false;
    ui_buzzer_reply_pending=false;ui_buzzer_sequence_reply=false;
}
static void start_control(uint32_t now) {
    shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u);
    shift_checkpoint=false;shift_key_inflight=false;
    if(!calibration_store_shift_clear()){printf("ERR|SHIFT|nvm-reset\n");return;}

    if (calibration_runtime_active()) { printf("ERR|GUARD|CALIBRATING\n"); return; }
    if (!arm_uart_mouse_ready()) {
        printf("ERR|GUARD|ARM|ready=0|version=%s|detail=%s\n", arm_uart_mouse_version(), arm_uart_mouse_fault());
        return;
    }
    if (guard_runtime_available()) {
        if (!light_sensor_present()) { printf("ERR|GUARD|NOSENSOR\n"); return; }
        abvm_stop(&vm, now);
        if (guard_runtime_start(now)) {
            if (!cycle_runtime_manual_start(now)) {
                guard_runtime_stop();release_all_actors(now);
                printf("ERR|CYCLE|RESET|guard=off\n");
                return;
            }
            printf("OK|GUARD|ON\n"); buzzer_play(BUZZER_CUE_START, now);
        }
        else printf("ERR|GUARD|START\n");
    } else if (abvm_start_route(&vm, GAME_ROUTE_ID, now))
        printf("CONTROL|start|route=Game|guard=unavailable\n");
    else printf("ERR|CONTROL|start\n");
}
static void stop_control(uint32_t now) {
    shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u);
    shift_checkpoint=false;

    buzzer_watchdog_alarm_stop();
    guard_runtime_stop(); abvm_stop(&vm, now); release_all_actors(now);
    pending_sound_whisper=false;
    ui_sound_watch_pending=false;ui_buzzer_reply_pending=false;
    ui_buzzer_sequence_reply=false;
    cycle_runtime_manual_stop();
    (void)calibration_store_shift_clear();
    printf("OK|GUARD|OFF\n"); buzzer_play(BUZZER_CUE_STOP, now);
}
static void toggle_pause(uint32_t now) {
    if (guard_runtime_running()) {
        if (guard_runtime_paused()) {
            bool watchdog=guard_runtime_watchdog_tripped();
            bool vm_ok = vm.status != ABVM_STATUS_PAUSED || abvm_resume(&vm, now);
            if (guard_runtime_resume() && vm_ok) {
                cycle_runtime_continue(now);
                if(watchdog)buzzer_watchdog_alarm_stop();
                printf("CONTROL|resume|guard=on|watchdog=%s|stage=%u|expected=%s|cycle=%u\n",
                       watchdog?"acknowledged":"off",guard_runtime_stage(),
                       guard_runtime_profile_name(guard_runtime_expected_profile()),
                       cycle_runtime_count());
                buzzer_play(BUZZER_CUE_RESUME, now);
            }
            else printf("ERR|CONTROL|resume\n");
        } else {
            bool vm_ok = vm.status != ABVM_STATUS_RUNNING || abvm_pause(&vm, now);
            if (guard_runtime_pause() && vm_ok) {
                cycle_runtime_hold(now);
                printf("CONTROL|pause|guard=on|stage=%u|expected=%s|cycle=%u\n",
                       guard_runtime_stage(),
                       guard_runtime_profile_name(guard_runtime_expected_profile()),
                       cycle_runtime_count());
                buzzer_play(BUZZER_CUE_PAUSE, now);
            }
            else printf("ERR|CONTROL|pause\n");
        }
    } else if (vm.status == ABVM_STATUS_PAUSED) {
        if (abvm_resume(&vm, now)) { cycle_runtime_continue(now);buzzer_watchdog_alarm_stop();printf("CONTROL|resume\n"); buzzer_play(BUZZER_CUE_RESUME, now); } else printf("ERR|CONTROL|resume\n");
    } else if (vm.status == ABVM_STATUS_RUNNING) {
        if (abvm_pause(&vm, now)) { cycle_runtime_hold(now);printf("CONTROL|pause\n"); buzzer_play(BUZZER_CUE_PAUSE, now); } else printf("ERR|CONTROL|pause\n");
    } else printf("CONTROL|pause-ignored|state=%s\n", abvm_status_name(vm.status));
}
static ButtonEvent button_event(Button *button,uint32_t now) {
    bool raw=!gpio_get(button->pin);
    if(raw!=button->raw){button->raw=raw;button->changed_at=now;}
    if(raw!=button->stable&&(uint32_t)(now-button->changed_at)>=BUTTON_DEBOUNCE_MS){
        button->stable=raw;
        if(raw){button->pressed_at=now;button->long_sent=false;button->consumed=false;return BUTTON_DOWN;}
        if(button->consumed)return BUTTON_NONE;
        return button->long_sent?BUTTON_NONE:BUTTON_SHORT;
    }
    if(button->stable&&!button->long_sent&&(uint32_t)(now-button->pressed_at)>=BUTTON_LONG_MS){
        button->long_sent=true;return BUTTON_LONG;
    }
    return BUTTON_NONE;
}
static void service_buttons(uint32_t now) {
    ButtonEvent yellow=button_event(&pause_button,now),blue=button_event(&start_button,now);
    if(calibration_runtime_active()){
        if(blue==BUTTON_SHORT)calibration_runtime_blue_short(now);
        else if(blue==BUTTON_LONG)calibration_runtime_blue_long(now);
        if(yellow==BUTTON_SHORT)calibration_runtime_yellow_short(now);
        else if(yellow==BUTTON_LONG)calibration_runtime_yellow_long(now);
        return;
    }
    bool running=guard_runtime_running()||vm.status==ABVM_STATUS_RUNNING||vm.status==ABVM_STATUS_PAUSED;
    if(blue==BUTTON_DOWN&&running){stop_control(now);start_button.consumed=true;}
    else if(blue==BUTTON_LONG&&!running){calibration_runtime_blue_long(now);start_button.consumed=true;}
    else if(blue==BUTTON_SHORT&&!running)start_control(now);
    if(yellow==BUTTON_LONG&&!running){calibration_runtime_yellow_long(now);pause_button.consumed=true;}
    else if(yellow==BUTTON_SHORT)toggle_pause(now);
}

static void execute_command(char *line, uint32_t now) {
    if (!strcmp(line, "PING")) printf("OK|PONG|combined-pico-guard-executor|native=abvm|abi=%u|format=%u|hid=on|uart=on|arm-ready=%u|arm-usb=%u|arm-ver=%s|profiles=%u|buzzer=legacy-calibration-gp6|role=brain\n", ABVM_VM_ABI, ABVM_FORMAT_VERSION, arm_uart_mouse_ready(), arm_uart_host_usb_state(), arm_uart_mouse_version(), guard_runtime_available() ? 9u : 0u);
    else if (!strcmp(line,"SHIFT?")) {
        if(shift_checkpoint&&shift_identity.phase==SHIFT_WAIT_REPLY)
            printf("OK|SHIFT-CHALLENGE|%08lx%s\n",(unsigned long)shift_identity.nonce,shift_identity.schedule_enabled?"|clock=1":"");
        else printf("ERR|SHIFT|not-waiting\n");
    }
    else if (!strncmp(line,"SHIFT!|",7)||!strncmp(line,"SHIFT2!|",8)) {
        bool with_clock=!strncmp(line,"SHIFT2!|",8);
        char *nonce_text=line+(with_clock?8:7),*hash_text=strchr(nonce_text,'|');
        uint16_t minute=0xffffu;
        uint8_t hash[32];bool valid=hash_text&&hash_text-nonce_text==8;
        uint32_t nonce=0u;
        if(valid) {
            *hash_text++='\0';char *end=NULL;
            if(with_clock){
                char *clock=strchr(hash_text,'|');
                if(!clock)valid=false;
                else {
                    *clock++='\0';size_t width=strlen(clock);
                    if(!width||width>4u)valid=false;
                    for(size_t i=0u;i<width;++i)if(clock[i]<'0'||clock[i]>'9')valid=false;
                    unsigned long value=strtoul(clock,&end,10);
                    if(!end||*end||value>=1440u)valid=false;else minute=(uint16_t)value;
                }
            }
            if(!valid){printf("ERR|SHIFT|invalid-clock\n");return;}
            nonce=(uint32_t)strtoul(nonce_text,&end,16);valid=end&&!*end&&strlen(hash_text)==64u;
            for(uint8_t i=0u;valid&&i<32u;++i) {
                char pair[3]={hash_text[i*2u],hash_text[i*2u+1u],0};
                unsigned long b=strtoul(pair,&end,16);valid=end&&end-pair==2&&b<=255u;hash[i]=(uint8_t)b;
            }
        }
        bool accepted=false;
        if(valid&&shift_checkpoint) {
            accepted=with_clock?
                shift_identity_reply_clock(&shift_identity,nonce,hash,minute,tud_cdc_connected()):
                shift_identity_reply(&shift_identity,nonce,hash,tud_cdc_connected());
            /* A fresh clock sample is the only wall-clock reference the portable
             * build ever gets; re-arm the autonomous wake deadline from it. */
            if(accepted&&with_clock) {
                wake_attempts_reset();
                /* A fresh sample is the definition of "the clock is restored",
                 * so it also retires any recovery budget spent while the board
                 * was running without one. */
                (void)calibration_store_wake_recovery_reset();
                if(wake_scheduler_sync(&wake_scheduler,now,minute))
                    printf("OK|WAKE|armed|window=%02u:%02u|in=%lu|lead=%u\n",
                           wake_scheduler.next_start/60u,wake_scheduler.next_start%60u,
                           (unsigned long)((wake_scheduler.deadline_ms-now)/60000u),
                           wake_scheduler.lead_minutes);
                else printf("EVT|WAKE|disarmed|reason=schedule-off\n");
            }
        }
        if(accepted) printf("OK|SHIFT-ACCEPTED|%08lx\n",(unsigned long)nonce);
        else printf("ERR|SHIFT|invalid-or-unknown-user\n");
    }
    else if (!strcmp(line, "STATUS")) print_status();
    else if (!strcmp(line, "WAKE?")) print_wake_state();
    else if (!strncmp(line, "WAKE!", 5)) {
        /* Operator hardware test: arm a one-shot deadline without waiting for a
         * real window, so the wake path can be exercised on demand.  The `!dry`
         * form wakes the host but starts no round, which is the safe first test.
         * The next accepted bridge clock sample replaces it with the schedule. */
        if (!strcmp(line + 5, "OFF")) {
            wake_scheduler_disarm(&wake_scheduler); wake_attempts_reset();
            wake_store_service(now, true);
            printf("OK|WAKE|manual|disarmed\n");
        } else {
            char *argument = line + 5; bool dry = false;
            char *bang = strchr(argument, '!');
            if (bang) {
                *bang = '\0';
                if (!strcmp(bang + 1, "dry")) dry = true; else argument = NULL;
            }
            char *end = NULL;
            unsigned long seconds = argument ? strtoul(argument, &end, 10) : 0ul;
            if (!argument || !end || *end || !seconds || seconds > 86400ul)
                printf("ERR|ARG|WAKE\n");
            else if (!(dry ? wake_scheduler_arm_dry(&wake_scheduler, now, (uint32_t)seconds * 1000u)
                           : wake_scheduler_arm_at(&wake_scheduler, now, (uint32_t)seconds * 1000u)))
                printf("ERR|ARG|WAKE\n");
            else {
                /* A fresh arm always starts from a clean phase machine, so a
                 * stale phase can never swallow the new deadline. */
                wake_phase=WAKE_PHASE_IDLE;wake_pulse_inflight=false;
                wake_woke_host=false;wake_dismiss_done=false;
                wake_dismiss_step=0u;wake_dismiss_started=0u;
                wake_host_awake_at=0u;wake_due_logged=false;wake_block_logged=false;
                wake_retry_at=now;
                wake_attempts_reset();
                wake_store_service(now, true);
                printf("OK|WAKE|manual|in=%lu|target=%02u:%02u|dry=%u\n", seconds,
                       wake_scheduler.next_start / 60u, wake_scheduler.next_start % 60u,
                       dry ? 1u : 0u);
            }
        }
    }
    else if (!strncmp(line, "TIME!", 5)) {
        /* A board that has never completed a shift check owns no wall clock at
         * all, so the authored schedule cannot arm and the first autonomous wake
         * of a window is impossible.  This one-shot stamp hands the board a
         * clock with no host software involved: the operator runs it once, or the
         * kitchen sends it right after flashing, and the board keeps time from
         * there.  The schedule itself now comes from the flashed program. */
        char *argument = line + 5;
        if (*argument == '|') ++argument;
        char *end = NULL;
        unsigned long hour = strtoul(argument, &end, 10);
        unsigned long minute = 0ul;
        bool valid = end && *end == ':' && hour < 24ul;
        if (valid) {
            char *minutes = end + 1;
            minute = strtoul(minutes, &end, 10);
            valid = end && !*end && (size_t)(end - minutes) <= 2u && minute < 60ul;
        }
        if (!valid) printf("ERR|ARG|TIME\n");
        else {
            wake_attempts_reset();
            (void)calibration_store_wake_recovery_reset();
            bool armed = wake_scheduler_sync(&wake_scheduler, now,
                                             (uint16_t)(hour * 60ul + minute));
            if (armed)
                printf("OK|TIME|set=%02lu:%02lu|window=%02u:%02u|in=%lu|lead=%u\n",
                       hour, minute, wake_scheduler.next_start / 60u,
                       wake_scheduler.next_start % 60u,
                       (unsigned long)((wake_scheduler.deadline_ms - now) / 60000u),
                       wake_scheduler.lead_minutes);
            else
                printf("OK|TIME|set=%02lu:%02lu|armed=0|schedule=%u\n",
                       hour, minute, wake_scheduler.enabled ? 1u : 0u);
        }
    }
    else if (!strncmp(line, "PWRBTN", 6)) {
        /* Operator hardware test for the power-button line.  An optocoupler across
         * a front-panel header is a real press on a real machine, so the pulse is
         * bounded on both sides, always released, and never left held.  Run it
         * with the target machine off, or with Windows set to ignore its power
         * button, and read the two EVT lines back. */
        char *argument=line+6;
        if(*argument=='|')++argument;
        char *end=NULL;
        unsigned long ms=(unsigned long)POWER_BUTTON_MS;
        bool valid=true;
        if(*argument) {
            ms=strtoul(argument,&end,10);
            valid=end&&!*end&&ms>=POWER_BUTTON_MIN_MS&&ms<=POWER_BUTTON_MAX_MS;
        }
        if(!POWER_BUTTON_ENABLED) printf("ERR|PWRBTN|disabled\n");
        else if(!valid) printf("ERR|ARG|PWRBTN\n");
        else if(!power_button_press(now,(uint16_t)ms)) printf("ERR|PWRBTN|busy\n");
        else printf("OK|PWRBTN|press|ms=%lu|count=%u\n",ms,(unsigned)power_button_presses);
    }
    else if (!strncmp(line, "LED!", 4)) {
        /* How bright a glow in a dark room should be is judged by eye and not in
         * code, so the duty cycle is a live setting: 0 turns the light off, 999
         * is the same bright level a press flash uses.  Volatile on purpose --
         * the next power-up returns the board to its documented default. */
        char *argument=line+4;
        if(*argument=='|')++argument;
        char *end=NULL;
        unsigned long level=*argument?strtoul(argument,&end,10):0ul;
        bool valid=*argument&&end&&!*end&&level<=STATUS_LED_PWM_WRAP;
        if(!valid) printf("ERR|ARG|LED\n");
        else {
            led_set_dim((uint16_t)level);
            printf("OK|LED|dim=%u\n",(unsigned)level);
        }
    }
    else if (!strcmp(line, "LUX?")) {
        uint32_t lux, age;
        if (light_sensor_latest(&lux, &age, now))
            printf("OK|LUX|lux=%lu.%lu|sensor=ok|age=%lu\n", (unsigned long)(lux / 10u), (unsigned long)(lux % 10u), (unsigned long)age);
        else printf("ERR|NOSENSOR|LUX\n");
    }
    else if (!strncmp(line, "LCAL|", 5)) {
        char *end = NULL; unsigned long duration = strtoul(line + 5, &end, 10);
        if (!end || *end || duration < 100u || duration > 60000u)
            printf("ERR|ARG|LCAL\n");
        else if (!light_sensor_present()) printf("ERR|NOSENSOR|LCAL\n");
        else if (!light_sensor_calibration_start((uint32_t)duration, now))
            printf("ERR|BUSY|LCAL\n");
    }
    else if (!strncmp(line, "SCAL|", 5)) {
        char *end = NULL; unsigned long duration = strtoul(line + 5, &end, 10);
        if (!end || *end || duration < 10u || duration > 1000u)
            printf("ERR|ARG|SCAL\n");
        else if (calibration_runtime_active() || ui_sound_calibration_pending)
            printf("ERR|BUSY|SCAL\n");
        else if (!arm_uart_sound_calibration_start(now,(uint16_t)duration))
            printf("ERR|BUSY|SCAL\n");
        else {
            ui_sound_calibration_pending=true;
            ui_sound_calibration_deadline=now+(uint32_t)duration+2000u;
        }
    }
    else if (!strncmp(line, "SETRES|", 7)) {
        char *middle=strchr(line+7,',');char *end=NULL;
        unsigned long width=strtoul(line+7,&end,10);
        if(!middle||end!=middle)printf("ERR|ARG|SETRES\n");
        else {
            unsigned long height=strtoul(middle+1,&end,10);
            if(!end||*end||!width||!height)printf("ERR|ARG|SETRES\n");
            else printf("OK|SETRES\n");
        }
    }
    else if (!strncmp(line, "MMOVE|", 6) ||
             !strncmp(line, "MCLICK|", 7) ||
             !strncmp(line, "MWHEEL|", 7) ||
             !strncmp(line, "MDOWN|", 6) ||
             !strncmp(line, "MUP|", 4) ||
             !strncmp(line, "KBDARM|", 7)) {
        /* Live Classroom mouse previews are write-only while discrete mouse
         * and explicitly arm-routed keyboard actions complete on the arm ACK. */
        const char *command=!strncmp(line,"KBDARM|",7)?line+7:line;
        ArmMouseSubmit result=arm_uart_mouse_submit_live(command,now);
        if(result!=ARM_MOUSE_ACCEPTED)
            printf("ERR|DIRECT|reason=%u|arm-ready=%u|arm-usb=%u\n",
                   result,arm_uart_mouse_ready(),arm_uart_host_usb_state());
    }
    else if (!strncmp(line, "KCOMBO|", 7) ||
             !strncmp(line, "KDOWN|", 6) ||
             !strncmp(line, "KUP|", 4) ||
             !strncmp(line, "KTEXT|", 6) ||
             !strncmp(line, "KBDPICO|", 8)) {
        const char *command=!strncmp(line,"KBDPICO|",8)?line+8:line;
        HidKeyboardSubmit result=hid_keyboard_submit_live(command,now);
        if(result!=HID_KEYBOARD_ACCEPTED)
            printf("ERR|KEYBOARD|reason=%u\n",result);
    }
    else if (!strncmp(line, "WSND|", 5)) {
        char *p=line+5,*end=NULL;unsigned long threshold=strtoul(p,&end,10);
        if(!end||*end!=',')printf("ERR|ARG|WSND\n");
        else {
            p=end+1;unsigned long minimum=strtoul(p,&end,10);
            if(!end||*end!=',')printf("ERR|ARG|WSND\n");
            else {
                p=end+1;unsigned long timeout=strtoul(p,&end,10);
                if(!end||*end||!threshold||threshold>1023u||!minimum||
                   minimum>65535u||!timeout||timeout>300000u)
                    printf("ERR|ARG|WSND\n");
                else if(ui_sound_watch_pending||
                        !arm_uart_sound_test_start(now,(uint16_t)threshold,
                                                  (uint16_t)minimum,(uint32_t)timeout))
                    printf("ERR|BUSY|WSND\n");
                else {
                    ui_sound_watch_pending=true;
                    ui_sound_watch_armed=false;
                }
            }
        }
    }
    else if (!strncmp(line,"TRGSND|",7)) {
        uint32_t v[8];
        if(!parse_csv_u32(line+7,v,8u)||!v[0]||v[0]>1023u||
           !v[1]||!v[2]||v[2]>300000u||v[3]<1u||v[3]>3u||
           v[4]>v[5]||v[6]>v[7]||v[7]>60000u)
            printf("ERR|ARG|TRGSND\n");
        else if(ui_sound_watch_pending||
                !arm_uart_sound_test_start(now,(uint16_t)v[0],
                                           (uint16_t)v[1],v[2]))
            printf("ERR|BUSY|TRGSND\n");
        else {
            ui_sound_watch_pending=true;ui_sound_watch_armed=true;
            ui_sound_trigger_button=(uint8_t)v[3];
            ui_sound_trigger_due=v[4]+(v[5]-v[4])/2u;
            ui_sound_trigger_hold_min=(uint16_t)v[6];
            ui_sound_trigger_hold_max=(uint16_t)v[7];
        }
    }
    else if (!strncmp(line,"WLUX|",5)||!strncmp(line,"TRGLUX|",7)) {
        bool armed=!strncmp(line,"TRGLUX|",7);
        uint32_t v[10];size_t count=armed?10u:5u;
        const char *args=line+(armed?7u:5u);
        if(!parse_csv_u32(args,v,count)||v[0]>v[1]||!v[3]||
           v[4]>1u||(armed&&(v[5]>255u||v[6]>v[7]||
                             v[8]>v[9]||v[9]>60000u)))
            printf("ERR|ARG|%s\n",armed?"TRGLUX":"WLUX");
        else {
            LightWatchSubmit result=light_sensor_live_start(
                v[0],v[1],v[2],v[3],(uint8_t)v[4],now);
            if(result!=LIGHT_WATCH_ACCEPTED)
                printf("ERR|LIGHT|reason=%u\n",result);
            else {
                ui_light_watch_pending=true;ui_light_watch_armed=armed;
                if(armed){
                    ui_light_trigger_key=(uint8_t)v[5];
                    ui_light_trigger_due=v[6]+(v[7]-v[6])/2u;
                    ui_light_trigger_hold_min=(uint16_t)v[8];
                    ui_light_trigger_hold_max=(uint16_t)v[9];
                }
            }
        }
    }
    else if (!strncmp(line, "BEEPSEQ|", 8)) {
        bool valid=true;uint8_t volume=100u,envelope=0u,count=0u;
        uint32_t total=0u;BuzzerTone sequence[8];
        char *style=line+8,*notes=strchr(style,'|');
        if(!notes)valid=false;
        if(valid){
            *notes++='\0';char *comma=strchr(style,',');
            if(!comma)valid=false;
            else {
                *comma++='\0';char *end=NULL;unsigned long parsed=strtoul(style,&end,10);
                if(!end||*end||!parsed||parsed>100u)valid=false;
                else volume=(uint8_t)parsed;
                if(!strcmp(comma,"sharp"))envelope=0u;
                else if(!strcmp(comma,"smooth"))envelope=1u;
                else if(!strcmp(comma,"fade-in"))envelope=2u;
                else if(!strcmp(comma,"fade-out"))envelope=3u;
                else valid=false;
            }
        }
        while(valid&&notes&&*notes){
            if(count>=8u){valid=false;break;}
            char *semi=strchr(notes,';');if(semi)*semi='\0';
            char *p=notes,*end=NULL;unsigned long hz=strtoul(p,&end,10);
            if(!end||*end!=','){valid=false;break;}
            p=end+1;unsigned long duration=strtoul(p,&end,10);
            if(!end||*end!=','){valid=false;break;}
            p=end+1;unsigned long gap=strtoul(p,&end,10);
            if(!end||*end||hz<30u||hz>20000u||!duration||duration>60000u||gap>60000u){
                valid=false;break;
            }
            if(total+duration+gap>180000u){valid=false;break;}
            sequence[count++]=(BuzzerTone){(uint16_t)hz,(uint16_t)duration,(uint16_t)gap};
            total+=(uint32_t)(duration+gap);
            notes=semi?semi+1:NULL;
        }
        if(!valid||!count)printf("ERR|ARG|BEEPSEQ\n");
        else if(ui_buzzer_reply_pending||buzzer_action_pending)printf("ERR|BUSY|BEEPSEQ\n");
        else {
            buzzer_play_sequence(sequence,count,volume,envelope,now);
            ui_buzzer_reply_pending=true;ui_buzzer_sequence_reply=true;
            ui_buzzer_reply_deadline=now+total;
        }
    }
    else if (!strncmp(line, "BEEP|", 5)) {
        char *p=line+5,*end=NULL;unsigned long hz=strtoul(p,&end,10);
        unsigned long duration=0u,volume=100u,envelope=0u;
        bool valid=end&&*end==',';
        if(valid){p=end+1;duration=strtoul(p,&end,10);}
        if(valid&&end&&*end==','){
            p=end+1;volume=strtoul(p,&end,10);
            if(end&&*end==','){
                p=end+1;
                if(!strcmp(p,"sharp"))envelope=0u;
                else if(!strcmp(p,"smooth"))envelope=1u;
                else if(!strcmp(p,"fade-in"))envelope=2u;
                else if(!strcmp(p,"fade-out"))envelope=3u;
                else valid=false;
                end=p+strlen(p);
            }
        }
        if(!valid||!end||*end||hz<30u||hz>20000u||!duration||
           duration>60000u||!volume||volume>100u)
            printf("ERR|ARG|BEEP\n");
        else if(ui_buzzer_reply_pending||buzzer_action_pending)
            printf("ERR|BUSY|BEEP\n");
        else {
            buzzer_play_tone_ex((uint16_t)hz,(uint16_t)duration,
                                (uint8_t)volume,(uint8_t)envelope,now);
            ui_buzzer_reply_pending=true;ui_buzzer_sequence_reply=false;
            ui_buzzer_reply_deadline=now+(uint32_t)duration;
        }
    }
    else if (!strcmp(line, "START") || !strcmp(line, "GUARD|ON")) start_control(now);
    else if (!strcmp(line, "PAUSE")) {
        bool vm_ok = vm.status != ABVM_STATUS_RUNNING || abvm_pause(&vm, now);
        bool guard_ok = !guard_runtime_running() || guard_runtime_pause();
        if (vm_ok && guard_ok) { printf("CONTROL|pause|guard=%u\n", guard_runtime_running()); buzzer_play(BUZZER_CUE_PAUSE, now); }
        else printf("ERR|CONTROL|pause\n");
    }
    else if (!strcmp(line, "RESUME")) {
        bool vm_ok = vm.status != ABVM_STATUS_PAUSED || abvm_resume(&vm, now);
        bool guard_ok = !guard_runtime_running() || guard_runtime_resume();
        if (vm_ok && guard_ok) { printf("CONTROL|resume|guard=%u\n", guard_runtime_running()); buzzer_play(BUZZER_CUE_RESUME, now); }
        else printf("ERR|CONTROL|resume\n");
    }
    else if (!strcmp(line, "STOP") || !strcmp(line, "GUARD|OFF") ||
             !strcmp(line, "HALT") || !strcmp(line, "HALT|SILENT")) stop_control(now);
    else if (!strcmp(line, "CALSTATUS"))
        printf("OK|CALSTATUS|revision=%lu|source=nvm-a-b|count=%u|mode=%u|last_error=none\n", (unsigned long)calibration_store_revision(), guard_runtime_available() ? 8u : 0u, calibration_runtime_mode());
    else if (!strcmp(line, "CALDUMP|LIGHT"))
        print_light_calibration_dump();
    else if (!strcmp(line, "WHISPER")) { if (abvm_interrupt_route(&vm, WHISPER_ROUTE_ID, now)) printf("CONTROL|interrupt|route=Whisper\n"); else printf("ERR|CONTROL|interrupt\n"); }
    else if (!strcmp(line, "WHISPER-REPEAT")) { if (abvm_interrupt_route(&vm, WHISPER_REPEAT_ROUTE_ID, now)) printf("CONTROL|interrupt|route=WhisperRepeat\n"); else printf("ERR|CONTROL|interrupt\n"); }
    else if (!strncmp(line, "SOUND ", 6)) { uint16_t profile = (uint16_t)strtoul(line + 6, NULL, 10); printf("%s|SOUND|profile=%u\n", abvm_sound_detected(&vm, profile, now) ? "OK" : "MISS", profile); }
    else if (*line) printf("ERR|COMMAND|unknown=%s\n", line);
}
static void service_cdc(uint32_t now) {
    while (tud_cdc_available()) { int value = tud_cdc_read_char();
        if (value == '\r' || value == '\n') { if (command_length) { command[command_length] = 0; execute_command(command, now); command_length = 0; } }
        else if (value >= 32 && value <= 126) { if (command_length + 1u < sizeof(command)) command[command_length++] = (char)value; else command_length = 0; }
    }
}
static void service_keyboard(uint32_t now) {
    uint8_t lane;
    if (hid_keyboard_service(now, &lane)) {
        if(lane==SHIFT_KEY_LANE) {
            shift_key_inflight=false;
        } else if (lane==BUFF_KEY_LANE) {
            if (buff_key_inflight) {
                buff_key_inflight=false;
                (void)game_buff_key_finished(&game_buffs,now);
            }
        } else if (!abvm_complete_action(&vm,lane,now))
            printf("ERR|HID|complete|lane=%u\n",lane);
    }
    char reply[24];
    if(hid_keyboard_take_live_reply(reply,sizeof(reply)))printf("%s\n",reply);
}
static bool light_cal_cue_active, sound_cal_cue_active;
static bool play_guard_profile_pattern(uint8_t profile_id,uint32_t now) {
    uint16_t hz[8],duration[8],gap[8];
    uint8_t count=0u,volume=100u,envelope=0u;
    BuzzerTone tones[8];
    if(!guard_runtime_calibration_pattern(profile_id,hz,duration,gap,
                                           &count,&volume,&envelope))
        return false;
    for(uint8_t i=0;i<count;++i)
        tones[i]=(BuzzerTone){hz[i],duration[i],gap[i]};
    buzzer_play_sequence(tones,count,volume,envelope,now);
    return true;
}
static uint8_t event_u8(const char *event,const char *key,uint8_t fallback) {
    const char *p=strstr(event,key); if(!p)return fallback;
    unsigned long value=strtoul(p+strlen(key),NULL,10);
    return value>255u?fallback:(uint8_t)value;
}
static void service_calibration_cue(const char *event,uint32_t now) {
    bool sound=strstr(event,"|SOUNDCAL|")!=NULL;
    bool error=!strncmp(event,"ERR|",4);
    uint8_t stage=event_u8(event,"stage=",1u);
    uint8_t selection=event_u8(event,sound?"id=":"profile=",
                               sound?1u:stage);
    uint8_t cue=sound?selection:guard_runtime_calibration_cue(selection);
    if(!sound){
        uint8_t volume=100u,envelope=0u;
        if(guard_runtime_calibration_style(selection,&volume,&envelope))
            buzzer_set_calibration_style(volume,envelope);
    }
    if(!sound){
        uint16_t hz[8],duration[8],gap[8];uint8_t count=0u,volume=100u,envelope=0u;
        BuzzerTone custom[8];
        if(guard_runtime_calibration_pattern(selection,hz,duration,gap,&count,&volume,&envelope)){
            for(uint8_t i=0;i<count;++i)custom[i]=(BuzzerTone){hz[i],duration[i],gap[i]};
            buzzer_set_calibration_custom(custom,count,volume,envelope);
        } else buzzer_set_calibration_custom(NULL,0u,volume,envelope);
    }
    if(error){buzzer_calibration_save_error(now);return;}
    if(strstr(event,"mode=exited")){buzzer_calibration_exit(now);if(sound)sound_cal_cue_active=false;else light_cal_cue_active=false;return;}
    if(strstr(event,"mode=ready")){bool *seen=sound?&sound_cal_cue_active:&light_cal_cue_active;if(!*seen){*seen=true;buzzer_calibration_enter(cue,sound,now);}else buzzer_calibration_position(cue,sound,now);return;}
    if(strstr(event,"mode=started")||strstr(event,"mode=silence")){buzzer_calibration_record_start(sound,now);return;}
    if(sound&&strstr(event,"mode=sound")){buzzer_calibration_sound_target(now);return;}
    if(!sound&&strstr(event,"mode=complete")){buzzer_calibration_stage_complete(cue,now);return;}
    if(strstr(event,"mode=saved")){
        if(!sound&&strstr(event,"|fit=1|"))
            buzzer_calibration_overlap_adjusted(now);
        else if(!sound&&stage==9u)
            buzzer_calibration_complete(now);
        else
            buzzer_calibration_save_success(now);
    }
}
static void service_light(uint32_t now) {
    light_sensor_service(&vm, now);
    bool live_detected;uint32_t live_lux;
    if(ui_light_watch_pending&&
       light_sensor_live_take(&live_detected,&live_lux)){
        ui_light_watch_pending=false;
        if(!live_detected)
            printf("ERR|TIMEOUT|%s|lux=%lu.%lu\n",
                   ui_light_watch_armed?"TRGLUX":"WLUX",
                   (unsigned long)(live_lux/10u),
                   (unsigned long)(live_lux%10u));
        else if(ui_light_watch_armed){
            ui_light_trigger_pending=true;
            ui_light_trigger_lux=live_lux;
            ui_light_trigger_due=now+ui_light_trigger_due;
        } else
            printf("OK|WLUX|MATCH|lux=%lu.%lu\n",
                   (unsigned long)(live_lux/10u),
                   (unsigned long)(live_lux%10u));
    }
    if(ui_light_trigger_pending&&(int32_t)(now-ui_light_trigger_due)>=0){
        HidKeyboardSubmit result=hid_keyboard_submit_trigger(
            ui_light_trigger_key,ui_light_trigger_hold_min,
            ui_light_trigger_hold_max,now);
        if(result==HID_KEYBOARD_ACCEPTED){
            ui_light_trigger_pending=false;
            printf("EVT|TRGLUX|DETECTED|lux=%lu.%lu\n",
                   (unsigned long)(ui_light_trigger_lux/10u),
                   (unsigned long)(ui_light_trigger_lux%10u));
        } else if(result!=HID_KEYBOARD_BUSY){
            ui_light_trigger_pending=false;
            printf("ERR|TRGLUX|KEY|reason=%u\n",result);
        }
    }
    if (light_sensor_take_fault()) {
        printf("ERR|LIGHT|sensor-lost\n");
        buzzer_play(BUZZER_CUE_ERROR, now);
        cycle_runtime_fail(5u);
        guard_runtime_stop(); abvm_stop(&vm, now);
    }
    calibration_runtime_service(now);
    char calibration_event[192];
    if (calibration_runtime_take_event(calibration_event,sizeof(calibration_event))) {
        printf("%s\n",calibration_event);
        service_calibration_cue(calibration_event, now);
    }
    if (!light_sensor_calibration_active()&&!calibration_runtime_active()) guard_runtime_service(&vm, now);
    GuardRuntimeEvent guard_event;
    if (guard_runtime_take_event(&guard_event)) {
        const char *profile = guard_runtime_profile_name(guard_event.profile_id);
        const char *expected=guard_runtime_profile_name(
            guard_runtime_expected_profile());
        uint32_t elapsed=guard_runtime_stage_elapsed(now);
        uint32_t watchdog=guard_runtime_watchdog_timeout_ms();
        if(guard_event.type==GUARD_EVENT_WATCHDOG_TRIPPED) {
            release_all_actors(now);
            cycle_runtime_hold(now);
            buzzer_watchdog_alarm_start(now);
            printf("ERR|GUARD|WATCHDOG|action=paused|reason=%s|stage=%u|expected=%s|elapsed-ms=%lu|timeout-ms=%lu|cycle=%u|resume=manual\n",
                   guard_event.reason,guard_runtime_stage(),expected,(unsigned long)elapsed,
                   (unsigned long)watchdog,cycle_runtime_count());
        } else if (guard_event.type == GUARD_EVENT_ROUTE) {
            if (strcmp(guard_event.reason,"start-at-current-state")) {
                if(guard_event.profile_id==7u)
                    buzzer_play(BUZZER_CUE_WHISPER,now);
                else if(guard_event.profile_id==8u)
                    buzzer_play(BUZZER_CUE_WHISPER_REPEAT,now);
                else if((guard_event.profile_id==6u||
                         guard_event.profile_id==9u)&&
                        play_guard_profile_pattern(guard_event.profile_id,now)) {
                    /* Targeted New/Repeat use their own editable profile
                     * motifs, so the two optical events never share a note. */
                }
                else buzzer_guard_transition(guard_event.profile_id, now);
                printf("BUZZER|cue=transition|profile=%s|stage=%u\n",
                       profile,guard_event.stage);
            }
            printf("EVT|GUARD|route=%u|profile=%s|stage=%u|context=%u|lux=%lu.%lu|reason=%s|expected=%s|stage-elapsed-ms=%lu|watchdog-ms=%lu|cycle=%u\n", guard_event.route_id, profile, guard_event.stage, guard_event.context, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason, expected, (unsigned long)elapsed, (unsigned long)watchdog, cycle_runtime_count());
        } else if (guard_event.type == GUARD_EVENT_FAULT) {
            buzzer_play(BUZZER_CUE_ERROR, now);
            cycle_runtime_fail(6u);
            printf("ERR|GUARD|%s\n", guard_event.reason);
        }
        else
            printf("EVT|GUARD|state=%s|stage=%u|lux=%lu.%lu|reason=%s|expected=%s|stage-elapsed-ms=%lu|watchdog-ms=%lu|cycle=%u\n", profile, guard_event.stage, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason, expected, (unsigned long)elapsed, (unsigned long)watchdog, cycle_runtime_count());
    }
    LightCalibrationResult result;
    if (!calibration_runtime_active() && light_sensor_calibration_take(&result)) {
        if (result.valid) {
            buzzer_play(BUZZER_CUE_CALIBRATION_OK, now);
            printf("OK|LCAL|min=%lu|max=%lu|avg=%lu|samples=%lu\n", (unsigned long)result.minimum_lux, (unsigned long)result.maximum_lux, (unsigned long)result.average_lux, (unsigned long)result.samples);
        } else { printf("ERR|NOSENSOR|LCAL\n"); buzzer_play(BUZZER_CUE_ERROR, now); }
    }
}
static void service_mouse(uint32_t now) {
    uint8_t completed_lane;
    if (arm_uart_mouse_service(now, &completed_lane)) {
        if(arm_uart_mouse_internal_completion(completed_lane)) {
            ambient_mouse_complete(now);
            wake_pulse_inflight=false;
        }
        else if((vm.status == ABVM_STATUS_RUNNING ||
                 vm.status == ABVM_STATUS_PAUSED) &&
                !abvm_complete_action(&vm, completed_lane, now))
            printf("ERR|ARM|complete|lane=%u\n", completed_lane);
    }
    char live_reply[96];
    if(arm_uart_mouse_take_live_reply(live_reply,sizeof(live_reply)))
        printf("%s\n",live_reply);
    if (ui_sound_calibration_pending) {
        uint16_t average,peak;
        if (arm_uart_sound_calibration_take(&average,&peak)) {
            ui_sound_calibration_pending=false;
            printf("OK|SCAL|avg=%u|max=%u\n",average,peak);
        } else if ((int32_t)(now-ui_sound_calibration_deadline)>=0) {
            ui_sound_calibration_pending=false;
            printf("ERR|TIMEOUT|SCAL\n");
        }
    }
    uint16_t profile, peak; bool detected;
    if (arm_uart_sound_take(&profile, &detected, &peak)) {
        if(profile==0u&&ui_sound_watch_pending) {
            ui_sound_watch_pending=false;
            if(detected&&ui_sound_watch_armed){
                ui_sound_trigger_pending=true;
                ui_sound_trigger_peak=peak;
                ui_sound_trigger_due=now+ui_sound_trigger_due;
            }
            else if(detected)printf("OK|WSND|DETECTED|peak=%u\n",peak);
            else printf("ERR|TIMEOUT|WSND|max=%u\n",peak);
            return;
        }
        if (detected) {
            GlobalWhisperProfile *selected=NULL;
            for(uint8_t i=0;i<2u;++i)
                if(whisper_profiles[i].enabled &&
                   peak>=whisper_profiles[i].threshold &&
                   peak<=whisper_profiles[i].maximum)
                    selected=&whisper_profiles[i];
            if (selected && whisper_interrupt_allowed()) {
                if(input_lock_active()) {
                    pending_sound_whisper=true;
                    pending_sound_profile=selected->id;
                    pending_sound_listener=profile;
                    pending_sound_peak=peak;
                    printf("PENDING|WHISPER|reason=input-locked|profile=%u|peak=%u\n",
                           selected->id,peak);
                    return;
                }
                if(start_sound_whisper(selected,profile,peak,now))
                    return;
            }
            uint16_t watch_profile=active_sound_watch_profile();
            bool accepted = watch_profile &&
                abvm_sound_detected(&vm, watch_profile, now);
            printf("%s|SOUND|profile=%u|peak=%u|threshold=%u|min=%u|config=%s|source=arm\n",
                   accepted ? "OK" : "MISS",watch_profile,peak,
                   arm_uart_sound_threshold(),arm_uart_sound_minimum(),
                   arm_uart_sound_uses_calibration()?"saved":"project");
        } else {
            printf("SOUND|timeout|profile=%u|peak=%u|threshold=%u|min=%u|config=%s|source=arm-global\n",
                   profile,peak,arm_uart_sound_threshold(),
                   arm_uart_sound_minimum(),
                   arm_uart_sound_uses_calibration()?"saved":"project");
            if(!global_sound_enabled)buzzer_play(BUZZER_CUE_TIMEOUT, now);
        }
    }
    if(ui_sound_trigger_pending&&(int32_t)(now-ui_sound_trigger_due)>=0){
        const char *button=ui_sound_trigger_button==2u?"right":
                           ui_sound_trigger_button==3u?"middle":"left";
        char click[48];
        snprintf(click,sizeof(click),"MCLICK|%s,1,%u,%u",button,
                 ui_sound_trigger_hold_min,ui_sound_trigger_hold_max);
        ArmMouseSubmit result=arm_uart_mouse_submit_internal(click,now);
        if(result==ARM_MOUSE_ACCEPTED){
            ui_sound_trigger_pending=false;
            printf("EVT|TRGSND|DETECTED|peak=%u\n",ui_sound_trigger_peak);
        } else if(result!=ARM_MOUSE_BUSY){
            ui_sound_trigger_pending=false;
            printf("ERR|TRGSND|CLICK|reason=%u\n",result);
        }
    }
    if (arm_uart_mouse_faulted()) {
        if (!arm_fault_reported) {
            printf("ERR|ARM|detail=%s|version=%s\n", arm_uart_mouse_fault(), arm_uart_mouse_version());
            buzzer_play(BUZZER_CUE_ERROR, now);
            cycle_runtime_fail(7u);
            arm_fault_reported = true;
        }
        if (vm.status != ABVM_STATUS_STOPPED && vm.status != ABVM_STATUS_FAULT) abvm_stop(&vm, now);
    }
}
static void service_buzzer_action(uint32_t now) {
    if(ui_buzzer_reply_pending&&(int32_t)(now-ui_buzzer_reply_deadline)>=0) {
        ui_buzzer_reply_pending=false;
        printf("%s\n",ui_buzzer_sequence_reply?"OK|BEEPSEQ":"OK|BEEP");
        ui_buzzer_sequence_reply=false;
    }
    if(!buzzer_action_pending||(int32_t)(now-buzzer_action_deadline)<0)return;
    buzzer_action_pending=false;
    if(!abvm_complete_action(&vm,buzzer_action_lane,now))
        printf("ERR|BUZZER|complete|lane=%u\n",buzzer_action_lane);
}
static const char *cycle_host_name(uint8_t state) {
    if(state==ARM_HOST_USB_UP)return "UP";
    if(state==ARM_HOST_USB_SUSPEND)return "SUSPEND";
    if(state==ARM_HOST_USB_DOWN)return "DOWN";
    return "UNKNOWN";
}
static const char *wake_recovery_reason_text(void) {
    if(wake_recovery_reason==WAKE_RECOVERY_REASON_CLOCK) return "clock";
    if(wake_recovery_reason==WAKE_RECOVERY_REASON_OWED) return "owed";
    return "none";
}
/* Everything the wake decision reads, in one reply, so an operator can see why
 * a deadline is still pending instead of inferring it from STATUS fields. */
static void print_wake_state(void) {
    bool armed=wake_scheduler_armed(&wake_scheduler);
    long due_ms=armed?(long)(int32_t)(wake_scheduler.deadline_ms-now_ms()):0l;
    printf("OK|WAKE|armed=%u|manual=%u|dry=%u|synced=%u|schedule=%u|target=%02u:%02u|due-ms=%ld|attempts=%u|phase=%u|recovery=%u|host=%s|pico-usb=%u|pico-rw=%u|recovery-reason=%s|pwr-presses=%u|host-boot-s=%u|pwr-grace=%u\n",
           armed?1u:0u,wake_scheduler.manual?1u:0u,wake_scheduler.dry?1u:0u,
           wake_scheduler.synced?1u:0u,wake_scheduler.enabled?1u:0u,
           wake_scheduler.next_start/60u,wake_scheduler.next_start%60u,due_ms,
           (unsigned)wake_attempts,(unsigned)wake_phase,(unsigned)wake_recovery_phase,
           cycle_host_name(arm_uart_host_usb_state()),
           pico_usb_suspended?1u:0u,pico_remote_wakeup_en?1u:0u,
           wake_recovery_reason_text(),(unsigned)power_button_presses,
           (unsigned)host_boot_learned_value(),(unsigned)power_button_grace_ms());
}
static void service_cycle_events(void) {
    CycleEvent event;
    while(cycle_runtime_take_event(&event)) {
        switch(event.type) {
            case CYCLE_EVENT_ARMED:
            case CYCLE_EVENT_RESUMED:
                printf("EVT|CYCLE|%s|seconds=%lu|range=%lu,%lu|count=%u\n",
                       event.type==CYCLE_EVENT_RESUMED?"resumed":"armed",
                       (unsigned long)event.seconds,
                       (unsigned long)event.range_min_seconds,
                       (unsigned long)event.range_max_seconds,event.count);
                break;
            case CYCLE_EVENT_ARMED_AT_BOOT:
                printf("EVT|CYCLE|armed-at-boot|cycle=%u\n",event.count);break;
            case CYCLE_EVENT_DEADLINE:
                printf("EVT|CYCLE|deadline|action=after|cycle=%u\n",event.count);break;
            case CYCLE_EVENT_AFTER_START:
                printf("EVT|CYCLE|after-start|route=%u|cycle=%u\n",
                       event.route_id,event.count);break;
            case CYCLE_EVENT_AFTER_COMPLETE:
                printf("EVT|CYCLE|after-complete|wait=usb-restart|down-seen=%u|cycle=%u\n",
                       event.down_seen,event.count);break;
            case CYCLE_EVENT_USB:
                printf("EVT|CYCLE|usb|state=%s|cycle=%u\n",
                       cycle_host_name(event.host_state),event.count);break;
            case CYCLE_EVENT_STARTUP_START:
                printf("EVT|CYCLE|startup-start|route=%u|cycle=%u|gate=%s\n",
                       event.route_id,event.count,
                       event.startup_gate==1u?"desktop-light":
                       event.startup_gate==2u?"usb-timeout-fallback":
                                                "unknown");break;
            case CYCLE_EVENT_FINISH_START:
                printf("EVT|CYCLE|finish-start|route=%u|cycle=%u\n",
                       event.route_id,event.count);break;
            case CYCLE_EVENT_FINISH_COMPLETE:
                printf("EVT|CYCLE|finish-complete|cycle=%u|state=idle\n",
                       event.count);break;
            case CYCLE_EVENT_CANCELLED:
                printf("EVT|CYCLE|cancelled|reason=manual-stop\n");break;
            case CYCLE_EVENT_BLOCKED:
                printf("EVT|CYCLE|blocked|reason=marker-or-limit\n");break;
            case CYCLE_EVENT_FAILED:
                printf("EVT|CYCLE|failed\n");break;
            default: break;
        }
    }
}
static void service_cycle(uint32_t now) {
    service_cycle_events();
    bool arm_seen=arm_uart_host_usb_seen();
    ArmHostUsbState host=arm_seen?arm_uart_host_usb_state():
        (tud_mounted()?ARM_HOST_USB_UP:ARM_HOST_USB_DOWN);
    uint32_t lux,age;
    bool desktop_ready=light_sensor_latest(&lux,&age,now)&&
        guard_runtime_profile_matches(1u,lux);
    CycleAction action=cycle_runtime_service(now,true,host,desktop_ready);
    service_cycle_events();
    if(action==CYCLE_ACTION_EXPIRE) {
        pending_sound_whisper=false;
        guard_runtime_stop();abvm_stop(&vm,now);release_all_actors(now);
        /* The active Game route was intentionally aborted.  Its actors still
         * owe physical release reports, but their completion tokens belong to
         * the old VM generation and must never be applied to the new After
         * route. */
        hid_keyboard_discard_completion();
        arm_uart_mouse_discard_completion();
        if(!cycle_runtime_begin_after(now)){service_cycle_events();return;}
        service_cycle_events();
        if(!abvm_start_route(&vm,cycle_runtime_after_route(),now)){
            cycle_runtime_fail(1u);service_cycle_events();
        }
    } else if(action==CYCLE_ACTION_START_STARTUP) {
        release_all_actors(now);
        hid_keyboard_discard_completion();
        arm_uart_mouse_discard_completion();
        if(abvm_start_route(&vm,cycle_runtime_startup_route(),now)) {
            cycle_runtime_begin_startup();service_cycle_events();
        } else {
            cycle_runtime_fail(2u);service_cycle_events();
        }
    } else if(action==CYCLE_ACTION_SHIFT_STALLED) {
        /* Never replay boot commands blindly if the authored restart did not
         * produce a ready desktop. Resume performs Startup's identity check. */
        release_all_actors(now);hid_keyboard_discard_completion();arm_uart_mouse_discard_completion();
        if(abvm_start_route(&vm,cycle_runtime_startup_route(),now)) {
            cycle_runtime_begin_startup();(void)abvm_pause(&vm,now);cycle_runtime_hold(now);
            buzzer_watchdog_alarm_start(now);
            printf("ERR|SHIFT|reason=switch-reboot-timeout|action=paused|resume=startup-recheck\n");
        }else{cycle_runtime_fail(9u);buzzer_watchdog_alarm_start(now);}
    } else if(action==CYCLE_ACTION_START_FINISH) {
        pending_sound_whisper=false;
        guard_runtime_stop();abvm_stop(&vm,now);release_all_actors(now);
        hid_keyboard_discard_completion();
        arm_uart_mouse_discard_completion();
        if(abvm_start_route(&vm,cycle_runtime_finish_route(),now)) {
            cycle_runtime_begin_finish();service_cycle_events();
        } else {
            cycle_runtime_fail(4u);service_cycle_events();
        }
    }
}
static void fail_shift_check(uint32_t now,const char *reason) {
    shift_identity_cancel(&shift_identity);shift_failure_paused=true;hid_keyboard_set_shift(0u);
    (void)abvm_pause(&vm,now);
    if(guard_runtime_running())(void)guard_runtime_pause();
    cycle_runtime_hold(now);
    buzzer_watchdog_alarm_start(now);
    printf("ERR|SHIFT|reason=%s|action=paused|resume=recheck\n",reason);
}
static void launch_shift_check(uint32_t now) {
    shift_identity_begin(&shift_identity,now);hid_keyboard_set_shift(0u);
    shift_launch_pending=true;shift_cue_started=false;shift_failure_paused=false;
    if(tud_cdc_connected())fail_shift_check(now,"serial-port-already-open");
    else printf("EVT|SHIFT|state=checking\n");
}
static void service_shift_check(uint32_t now) {
    /* A fresh Startup means the OS/round has changed. Temporary optical
     * interrupts do not invalidate the current round's identity. */
    if(vm.route_generation!=shift_generation) {
        shift_generation=vm.route_generation;
        if(vm.route_id==3u) {
            shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u);
        }
        if(shift_checkpoint){shift_checkpoint=false;shift_identity_forget(&shift_identity);}
    }
    if(vm.status==ABVM_STATUS_STOPPED||vm.status==ABVM_STATUS_FAULT) {
        shift_checkpoint=false;shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u);return;
    }
    if(!shift_checkpoint||vm.status!=ABVM_STATUS_RUNNING||vm.pending_release)return;
    if(shift_identity.phase==SHIFT_FAILED) {
        if(!shift_failure_paused)fail_shift_check(now,shift_identity.reason?shift_identity.reason:"failed");
        else { buzzer_watchdog_alarm_stop();launch_shift_check(now); }
        return;
    }
    if(shift_launch_pending&&!hid_keyboard_locked()&&!arm_uart_mouse_busy()) {
        AbvmEvent key={0};key.opcode=ABVM_OP_KEY;key.lane=SHIFT_KEY_LANE;
        key.flags=shift_identity.key_count;key.operand_c=shift_identity.hold_min;key.operand_d=shift_identity.hold_max;
        for(uint8_t i=0u;i<key.flags;++i)key.operand_b|=(uint32_t)shift_identity.keys[i]<<(8u*i);
        HidKeyboardSubmit result=hid_keyboard_submit(&vm,&key,now);
        if(result==HID_KEYBOARD_ACCEPTED){shift_launch_pending=false;shift_key_inflight=true;}
        else if(result!=HID_KEYBOARD_BUSY){fail_shift_check(now,"hotkey-submit");return;}
    }
    ShiftPhase phase=shift_identity_tick(&shift_identity,now,tud_cdc_connected());
    if(phase==SHIFT_FAILED){fail_shift_check(now,shift_identity.reason?shift_identity.reason:"failed");return;}
    if(phase==SHIFT_OK&&!shift_key_inflight) {
        if(!shift_cue_started&&shift_identity.schedule_enabled) {
            ShiftKind expected=shift_identity_expected(&shift_identity);
            if(expected!=SHIFT_GLOBAL&&expected!=shift_identity.selected) {
                uint16_t route=expected==SHIFT_DAY?shift_identity.day_route:shift_identity.night_route;
                if(calibration_store_shift_attempts()>=shift_identity.max_attempts){
                    fail_shift_check(now,"switch-attempt-limit");return;
                }
                /* Persist attempt BEFORE executing any authored boot/restart steps. */
                if(!cycle_runtime_begin_shift(route,(uint8_t)expected,shift_identity.max_attempts,now)){
                    fail_shift_check(now,"switch-marker-write");return;
                }
                shift_checkpoint=false;guard_runtime_stop();abvm_stop(&vm,now);release_all_actors(now);
                hid_keyboard_discard_completion();arm_uart_mouse_discard_completion();
                hid_keyboard_set_shift(0u);shift_identity_forget(&shift_identity);
                printf("EVT|SHIFT|state=switching|target=%s|attempt=%u|route=%u\n",
                       expected==SHIFT_DAY?"day":"night",calibration_store_shift_attempts(),route);
                if(!abvm_start_route(&vm,route,now)){cycle_runtime_fail(8u);buzzer_watchdog_alarm_start(now);}
                return;
            }
            if(calibration_store_shift_target()&&
               (expected!=SHIFT_GLOBAL||calibration_store_shift_target()==(uint8_t)shift_identity.selected)) {
                /* A gap NEVER blocks a known user. Preserve the old round count
                 * if a pending destination has not been reached yet. */
                if(!cycle_runtime_shift_confirmed(now)){fail_shift_check(now,"switch-reset-write");return;}
                printf("EVT|SHIFT|state=switch-confirmed|round=1|attempts=0\n");
            }
            printf("OK|SHIFT-SCHEDULE|v=2|minute=%u|expected=%s\n",shift_identity.minute,
                   expected==SHIFT_DAY?"day":expected==SHIFT_NIGHT?"night":"gap-ignored");
        }
        if(!shift_cue_started) {
            BuzzerTone cue[2];
            bool day=shift_identity.selected==SHIFT_DAY;
            cue[0]=(BuzzerTone){day?880u:1320u,180u,80u};
            cue[1]=(BuzzerTone){day?1320u:660u,260u,80u};
            buzzer_play_sequence(cue,2u,100u,0u,now);
            shift_cue_started=true;shift_cue_until=now+600u;
            hid_keyboard_set_shift((uint8_t)shift_identity.selected);
            printf("EVT|SHIFT|state=verified|shift=%s|bridge=closed\n",day?"day":"night");
        }
        if((int32_t)(now-shift_cue_until)>=0) {
            shift_checkpoint=false;
            if(!abvm_complete_action(&vm,shift_lane,now)) {
                printf("ERR|SHIFT|checkpoint-completion\n");abvm_stop(&vm,now);
            }
        }
    }
}

static void service_game_buffs(uint32_t now) {
    uint32_t delta=now-game_age_at;game_age_at=now;
    if (buff_generation!=vm.route_generation) {
        game_buff_end_game(&game_buffs);buff_checkpoint=false;
        buff_generation=vm.route_generation;
    }
    bool live_game=vm.route_id==GAME_ROUTE_ID && vm.status==ABVM_STATUS_RUNNING;
    bool saved_game=vm.suspended.valid && vm.suspended.route_id==GAME_ROUTE_ID;
    if (live_game && !buff_checkpoint && game_buffs.session) {
        if (delta<86400000u && game_fishing_elapsed<86400000u-delta)
            game_fishing_elapsed+=delta;
    }
    arm_uart_mouse_set_game_elapsed(game_fishing_elapsed);
    if (vm.status==ABVM_STATUS_IDLE || vm.status==ABVM_STATUS_FAULT ||
        (!live_game && vm.status!=ABVM_STATUS_PAUSED && !saved_game)) {
        if(game_buffs.session)game_buff_end_game(&game_buffs);
        buff_checkpoint=false;return;
    }
    if (game_buffs.session && now-buff_status_at>=5000u) {
        buff_status_at=now;
        for(uint8_t i=0u;i<game_buffs.count;++i)
            printf("EVT|BUFF|index=%u|remaining-ms=%lu|consumed=%u|pending=%u\n",
                   i,(unsigned long)game_buff_remaining(&game_buffs,i,now),
                   (game_buffs.consumed_mask&(1u<<i))?1u:0u,
                   game_buff_pending(&game_buffs,now)?1u:0u);
    }
    if (!buff_checkpoint) return;
    GameBuffGate gate={live_game,vm.status==ABVM_STATUS_PAUSED,
        cycle_runtime_restart_critical(),!input_lock_active(),true};
    /* Do not send while an interrupt has entered but RELEASE_ALL is pending. */
    if(vm.pending_release)gate.optical_priority=true;
    GameBuffEvent event=game_buff_service(&game_buffs,now,gate);
    if(event.kind==GAME_BUFF_EVENT_CONSUMED)
        printf("EVT|BUFF|index=%u|state=command-completed|next-ms=%lu\n",
               event.index,(unsigned long)((int32_t)(event.next_due-now)>0?event.next_due-now:0u));
    if(event.kind==GAME_BUFF_EVENT_KEY_REQUEST) {
        const GameBuffConfig *c=&game_buffs.config[event.index];
        AbvmEvent key={0};key.opcode=ABVM_OP_KEY;key.lane=BUFF_KEY_LANE;
        key.flags=c->key_count;key.operand_c=key.operand_d=event.hold_ms;
        for(uint8_t i=0u;i<c->key_count;++i)key.operand_b|=(uint32_t)c->keys[i]<<(8u*i);
        HidKeyboardSubmit result=hid_keyboard_submit(&vm,&key,now);
        if(result==HID_KEYBOARD_ACCEPTED) {
            buff_key_inflight=game_buff_key_accepted(&game_buffs);
            printf("EVT|BUFF|index=%u|state=key-accepted\n",event.index);
        } else if(result!=HID_KEYBOARD_BUSY) {
            printf("ERR|BUFF|key-submit=%u\n",result);
            abvm_stop(&vm,now);release_all_actors(now);game_buff_end_game(&game_buffs);
            buff_checkpoint=false;
        }
    }
    if(live_game && !vm.pending_release && !game_buff_pending(&game_buffs,now)) {
        buff_checkpoint=false;
        if(!abvm_complete_action(&vm,buff_lane,now)) {
            printf("ERR|BUFF|checkpoint-complete\n");abvm_stop(&vm,now);
        }
    }
}

static void service_vm(uint32_t now) {
    if(shift_checkpoint&&!vm.pending_release)return;
    if(buff_checkpoint && vm.route_id==GAME_ROUTE_ID && !vm.pending_release)return;
    /* Keep the foreground VM at its exact PC while Ambient owns the ARM mouse.
     * Route clocks remain wall-clock based, so overdue work resumes immediately
     * after the internal lane completes. */
    if(ambient_mouse_inflight)return;
    if (arm_uart_mouse_releasing()) {
        return;
    }
    AbvmEvent event = abvm_tick(&vm, now);
    switch (event.type) {
        case ABVM_EVENT_ACTION: {
            if(event.opcode==ABVM_OP_SHIFT_CHECK) {
                const uint8_t *payload;uint32_t size;
                if((vm.route_id!=1u&&vm.route_id!=3u)||shift_checkpoint||
                   !abvm_constant(&vm,event.operand_a,ABVM_CONST_SHIFT,&payload,&size)||
                   !shift_identity_load(&shift_identity,payload,size,now^local_u32(vm.header.program_sha256))) {
                    printf("ERR|SHIFT|invalid-descriptor\n");abvm_stop(&vm,now);break;
                }
                shift_lane=event.lane;shift_checkpoint=true;
                wake_scheduler_configure(&wake_scheduler,shift_identity.schedule_enabled,
                                         shift_identity.day_start,shift_identity.day_end,
                                         shift_identity.night_start,shift_identity.night_end);
                shift_generation=vm.route_generation;launch_shift_check(now);break;
            }
            if(event.opcode==ABVM_OP_SHIFT_TYPE)event.opcode=ABVM_OP_TYPE;
            if(event.opcode==ABVM_OP_BUFF) {
                const uint8_t *payload;uint32_t size;
                if(vm.route_id!=GAME_ROUTE_ID || buff_checkpoint ||
                    !abvm_constant(&vm,event.operand_a,ABVM_CONST_BUFF,&payload,&size) ||
                    (event.flags && !game_buff_load(&game_buffs,payload,size,
                        now^local_u32(vm.header.program_sha256))) ||
                    (!event.flags && !game_buffs.session)) {
                    printf("ERR|BUFF|checkpoint-invalid\n");abvm_stop(&vm,now);break;
                }
                if(event.flags) {
                    game_buff_new_game(&game_buffs);game_fishing_elapsed=0u;
                    arm_uart_mouse_set_game_elapsed(0u);game_age_at=now;
                    printf("EVT|BUFF|state=new-game|count=%u\n",game_buffs.count);
                }
                buff_lane=event.lane;buff_checkpoint=true;break;
            }
            ArmMouseSubmit mouse = arm_uart_mouse_submit(&vm, &event, now);
            if(event.opcode==ABVM_OP_BEEP) {
                if(buzzer_action_pending) {
                    printf("ERR|BUZZER|busy|lane=%u\n",event.lane);
                    abvm_stop(&vm,now); break;
                }
                uint8_t volume=(uint8_t)(event.operand_c&0xffu);
                uint8_t envelope=(uint8_t)((event.operand_c>>8)&0xffu);
                if(!volume)volume=100u; /* ABI-1 images produced before volume support */
                buzzer_play_tone_ex(event.operand_a,(uint16_t)event.operand_b,
                                    volume,envelope,now);
                buzzer_action_pending=true;buzzer_action_lane=event.lane;
                buzzer_action_deadline=now+event.operand_b;
                printf("BUZZER|accepted|lane=%u|hz=%u|duration=%lu|volume=%u|envelope=%u\n",
                       event.lane,event.operand_a,(unsigned long)event.operand_b,
                       volume,envelope);
                break;
            }
            if (mouse == ARM_MOUSE_ACCEPTED) { printf("ARM|mouse|accepted|lane=%u\n", event.lane); break; }
            if (mouse != ARM_MOUSE_UNSUPPORTED) { printf("ERR|ARM|submit|lane=%u|reason=%u\n", event.lane, mouse); buzzer_play(BUZZER_CUE_ERROR, now); abvm_stop(&vm, now); break; }
            HidKeyboardSubmit result = hid_keyboard_submit(&vm, &event, now);
            if (result == HID_KEYBOARD_ACCEPTED) printf("HID|keyboard|accepted|lane=%u|op=%u\n", event.lane, event.opcode);
            else if (result == HID_KEYBOARD_UNSUPPORTED) { printf("ACTION|stub|lane=%u|op=%u|a=%u|b=%lu|c=%lu|d=%lu\n", event.lane, event.opcode, event.operand_a, (unsigned long)event.operand_b, (unsigned long)event.operand_c, (unsigned long)event.operand_d); if (!abvm_complete_action(&vm, event.lane, now)) printf("ERR|ACTION|complete\n"); }
            else { printf("ERR|HID|submit|lane=%u|op=%u|reason=%u\n", event.lane, event.opcode, result); abvm_stop(&vm, now); } break;
        }
        case ABVM_EVENT_WATCH_ARMED: {
            LightWatchSubmit light = light_sensor_arm(&vm, &event, now);
            if (light == LIGHT_WATCH_ACCEPTED) {
                printf("WATCH|armed|lane=%u|constant=%u|timeout=%lu|source=bh1750\n", event.lane, event.constant_id, (unsigned long)event.operand_b);
                break;
            }
            if (light != LIGHT_WATCH_UNSUPPORTED) {
                printf("ERR|LIGHT|arm|lane=%u|constant=%u|reason=%u\n", event.lane, event.constant_id, light);
                abvm_stop(&vm, now); break;
            }
            if(global_sound_enabled) {
                const uint8_t *descriptor;uint32_t descriptor_size;
                uint16_t profile=0u;
                if(abvm_constant(&vm,event.constant_id,ABVM_CONST_SOUND,
                                 &descriptor,&descriptor_size)&&descriptor_size==8u)
                    profile=local_u16(descriptor);
                printf("WATCH|armed|lane=%u|profile=%u|timeout=%lu|source=arm-global\n",
                       event.lane,profile,(unsigned long)event.operand_b);
                break;
            }
            ArmSoundSubmit sound = arm_uart_sound_arm(&vm, &event, now);
            if (sound == ARM_SOUND_ACCEPTED)
                printf("WATCH|armed|lane=%u|profile=%u|timeout=%lu|threshold=%u|min=%u|config=%s|source=arm\n",
                       event.lane,event.operand_a,(unsigned long)event.operand_b,
                       arm_uart_sound_threshold(),arm_uart_sound_minimum(),
                       arm_uart_sound_uses_calibration()?"saved":"project");
            else { printf("ERR|ARM|sound-arm|lane=%u|profile=%u|reason=%u\n", event.lane, event.operand_a, sound); abvm_stop(&vm, now); }
            break;
        }
        case ABVM_EVENT_RELEASE_ALL: release_all_actors(now); printf("HID|release-all|queued\n"); break;
        case ABVM_EVENT_INTERRUPT_RESUME: printf("CONTROL|interrupt-resume|route=%u\n", vm.route_id); break;
        case ABVM_EVENT_ROUTE_COMPLETE: {
            release_all_actors(now);printf("ROUTE|complete|route=%u\n",event.route_id);
            (void)guard_runtime_route_complete(&vm,event.route_id,now);
            if(cycle_runtime_route_complete(event.route_id,now)) {
                printf("EVT|CYCLE|startup-complete|next=login-or-dc|desktop=skip|cycle=%u\n",
                       cycle_runtime_count());
                if(!guard_runtime_start_after_restart(now))cycle_runtime_fail(3u);
                else printf("EVT|GUARD|watchdog=armed|stage=%u|expected=%s|timeout-ms=%lu|cycle=%u\n",
                            guard_runtime_stage(),
                            guard_runtime_profile_name(
                                guard_runtime_expected_profile()),
                            (unsigned long)guard_runtime_watchdog_timeout_ms(),
                            cycle_runtime_count());
                service_cycle_events();
            } else service_cycle_events();
            break;
        }
        case ABVM_EVENT_FAULT: cycle_runtime_fail(4u);release_all_actors(now); buzzer_play(BUZZER_CUE_ERROR, now); printf("ERR|ABVM|%s\n", event.message ? event.message : "fault"); break;
        default: break;
    }
}
/* Hostless shift wake.  Runs only while no round is active: a running macro is
 * already driving HID traffic, which keeps the host awake by itself.  The
 * Arduino board reports the host's USB suspend state on the private UART link,
 * so a wake is verified instead of assumed. */
static void wake_attempts_reset(void) {
    wake_attempts=0u;wake_retry_at=0u;
}
/* The host is asleep when the Arduino board reports the bus suspended.  Until
 * that first sample arrives, the Pico's own suspend flag is the only evidence,
 * and the Arduino board re-announces its state every two seconds. */
static bool wake_host_asleep(void) {
    if(arm_uart_host_usb_seen()) return arm_uart_host_usb_state()==ARM_HOST_USB_SUSPEND;
    return pico_usb_suspended;
}
static bool wake_host_up(void) { return arm_uart_host_usb_state()==ARM_HOST_USB_UP; }
static const char *wake_clock_text(char *buffer,size_t capacity) {
    uint16_t minute=0u;
    if(!wake_scheduler_wall_minute(&wake_scheduler,now_ms(),&minute)) return "unknown";
    snprintf(buffer,capacity,"%02u:%02u",minute/60u,minute%60u);
    return buffer;
}
/* Only the recovery decision is persisted, and only when it changes: a sector
 * erase per loop iteration would wear the slot out in days. */
static WakeStoreState wake_store_snapshot(void) {
    WakeStoreState state;
    if(!calibration_store_wake_get(&state)) memset(&state,0,sizeof(state));
    state.host_asleep=wake_host_asleep();
    state.pending=wake_scheduler_armed(&wake_scheduler)||wake_recovery_phase!=WAKE_RECOVERY_IDLE;
    state.next_start=wake_scheduler.next_start;
    /* A sample taken this session has to travel with the decision, or the next
     * power cut would find the record still empty and wait the default again. */
    if(host_boot_learned_s>state.host_boot_s) state.host_boot_s=host_boot_learned_s;
    return state;
}
static void wake_store_service(uint32_t now,bool force) {
    WakeStoreState state=wake_store_snapshot();
    /* A recovery runs on the record that armed it, so that record has to outlive
     * the recovery's own wait.  The live reading this snapshot carries cannot
     * stand in for it: on a machine that is off there is no bus and no arm
     * report, so the live reading is always "the host is not asleep" -- and
     * writing it back over the arming record disarms the one press the recovery
     * exists to make, exactly after the grace has been waited out for it.  The
     * arming flags are held while a recovery is armed, and the record returns to
     * live readings the moment the recovery ends. */
    if(wake_recovery_phase!=WAKE_RECOVERY_IDLE) {
        state.host_asleep=wake_store_last.host_asleep;
        state.pending=wake_store_last.pending;
    }
    bool changed=state.host_asleep!=wake_store_last.host_asleep||
                 state.pending!=wake_store_last.pending||
                 state.recovery_attempts!=wake_store_last.recovery_attempts||
                 state.next_start!=wake_store_last.next_start||
                 state.host_boot_s!=wake_store_last.host_boot_s;
    if(!changed&&!wake_store_dirty) return;
    if(!force&&(int32_t)(now-wake_store_next_at)<0) { wake_store_dirty=true; return; }
    if(!calibration_store_wake_set(&state)) { wake_store_dirty=true; return; }
    wake_store_last=state;wake_store_dirty=false;
    wake_store_next_at=now+WAKE_STORE_MIN_INTERVAL_MS;
}
static bool wake_pulse_pico(void) {
#if WAKE_USE_PICO_WAKEUP
    /* TinyUSB only drives resume when the host armed DEVICE_REMOTE_WAKEUP, and
     * driving it on an unsuspended bus would be a protocol violation. */
    if(!pico_usb_suspended||!pico_remote_wakeup_en) return false;
    return tud_remote_wakeup();
#else
    return false;
#endif
}
static ArmMouseSubmit wake_pulse_arm(uint32_t now) {
#if WAKE_USE_ARM_PULSE
    return arm_uart_mouse_submit_internal(WAKE_PULSE_COMMAND,now);
#else
    (void)now;return ARM_MOUSE_UNSUPPORTED;
#endif
}
/* The power-button line is driven low before anything else in main() and is only
 * ever high for the length of one bounded press.  A floating or glitching pin
 * here is not a cosmetic problem: it is a real press on a real machine. */
/* The board's own LED is the only thing on this hardware that can say "alive"
 * without a host to read it: the console is the machine's own USB, so an operator
 * standing in front of a machine that is off has no other signal at all.  It
 * therefore burns dim and steady while the board runs -- a glow that can be found
 * from across the room without being a beacon in it, and one that draws a few
 * percent of what a lit LED draws, which is what keeps it alive for years of
 * shifts.  A pulse every two seconds was the earlier answer and it is worse on
 * both counts: brighter, and a moving light in the corner of the room.  Only two
 * events are worth a bright flash -- this board's own power-up, so a board that
 * just restarted can be told from one that has been running, and the moment it
 * presses the machine's power button, which is otherwise invisible from outside
 * the machine it is aimed at.  The duty cycle is a live setting (`LED!<0-999>`,
 * 0 turns the light off) because the right level is judged by eye, not in code.
 * GP25 is the Pico's own LED and is otherwise unused; it is PWM slice 4 channel
 * B, whose slice partner GP24 this firmware does not use.  The numbers are with
 * the pin constants at the top of this file. */
static uint16_t led_dim_level=STATUS_LED_DIM_LEVEL;
static uint32_t led_bright_until;
static bool led_bright;
static void led_level(uint16_t level) {
    pwm_set_gpio_level(STATUS_LED_PIN,level);
}
static void led_dim(void) {
    led_bright=false;
    led_level(led_dim_level);
}
static void led_flash(uint32_t now,uint32_t hold_ms) {
    led_bright=true;
    led_bright_until=now+hold_ms;
    led_level(STATUS_LED_PWM_WRAP);
}
static void led_set_dim(uint16_t level) {
    led_dim_level=level;
    if(!led_bright) led_dim();
}
static void service_status_led(uint32_t now) {
    /* A flash is the exception and never the state: whatever was flashed for,
     * the board goes back to its glow and stays there. */
    if(led_bright&&(int32_t)(now-led_bright_until)>=0) led_dim();
}
static void status_led_init(uint32_t now) {
    gpio_set_function(STATUS_LED_PIN,GPIO_FUNC_PWM);
    pwm_config config=pwm_get_default_config();
    pwm_config_set_wrap(&config,STATUS_LED_PWM_WRAP);
    pwm_init(pwm_gpio_to_slice_num(STATUS_LED_PIN),&config,true);
    led_dim();
    led_flash(now,STATUS_LED_BOOT_MS);
}
static void power_button_init(void) {
#if POWER_BUTTON_ENABLED
    gpio_init(POWER_BUTTON_PIN);
    gpio_pull_down(POWER_BUTTON_PIN);   /* the optocoupler stays dark across the
                                         * direction change below */
    gpio_put(POWER_BUTTON_PIN, 0);
    gpio_set_dir(POWER_BUTTON_PIN, GPIO_OUT);
#endif
    power_button_held=false;
    power_button_release_at=0u;
    power_button_presses=0u;
}
static bool power_button_press(uint32_t now,uint16_t hold_ms) {
#if POWER_BUTTON_ENABLED
    if(power_button_held) return false;
    if(hold_ms<POWER_BUTTON_MIN_MS) hold_ms=POWER_BUTTON_MIN_MS;
    if(hold_ms>POWER_BUTTON_MAX_MS) hold_ms=POWER_BUTTON_MAX_MS;
    gpio_put(POWER_BUTTON_PIN,1);
    power_button_held=true;
    power_button_release_at=now+hold_ms;
    power_button_presses=(uint8_t)(power_button_presses+1u);
    /* A press is a real action on a machine this board cannot see: the LED says it
     * happened, for the operator standing next to that machine. */
    led_flash(now,STATUS_LED_PRESS_MS);
    printf("EVT|PWRBTN|press|ms=%u|count=%u\n",(unsigned)hold_ms,(unsigned)power_button_presses);
    return true;
#else
    (void)now;(void)hold_ms;return false;
#endif
}
/* Released from the main loop, never from a blocking wait: a press that outlives
 * POWER_BUTTON_MAX_MS is a forced power-off. */
static void service_power_button(uint32_t now) {
    if(!power_button_held) return;
    if((int32_t)(now-power_button_release_at)<0) return;
    power_button_held=false;
    gpio_put(POWER_BUTTON_PIN,0);
    printf("EVT|PWRBTN|release\n");
}
static void wake_report_pulse(uint8_t attempt,bool pico,ArmMouseSubmit arm_result) {
    char clock[16];
    printf("EVT|WAKE|state=pulse|attempt=%u|wall=%s|target=%02u:%02u|lead=%u|pico-rw=%u|arm=%u|usb=%u\n",
           (unsigned)attempt,wake_clock_text(clock,sizeof(clock)),
           wake_scheduler.next_start/60u,wake_scheduler.next_start%60u,
           wake_scheduler.lead_minutes,pico?1u:0u,
           arm_result==ARM_MOUSE_ACCEPTED?1u:0u,(unsigned)arm_uart_host_usb_state());
}
/* After a board reset the monotonic deadline is gone, but the persisted record
 * still says the host was asleep with a wake owed.  The only route back to a wall
 * clock is to bring the host up once: the bridge then re-sends the sample, the
 * authored deadline is re-armed, and the machine is free to sleep again until the
 * real window.  The pulse is bounded by a persisted attempt counter so a brownout
 * loop can never become a wake storm, and an already-awake host cancels it. */
/* A pending deadline that nothing can fire is the one failure an operator cannot
 * see, because it writes no line at all.  Every blocker therefore names itself
 * once per deadline, with the state it was blocked in. */
static void wake_report_blocked(const char *reason,uint32_t now) {
    if(wake_block_logged) return;
    if(!wake_scheduler_armed(&wake_scheduler)||!wake_scheduler_due(&wake_scheduler,now)) return;
    wake_block_logged=true;
    printf("ERR|WAKE|blocked|reason=%s|state=%s|phase=%u|recovery=%u\n",
           reason,abvm_status_name(vm.status),(unsigned)wake_phase,
           (unsigned)wake_recovery_phase);
}
/* The learned cold start is what the grace has to clear.  A stored value that
 * could not have come from a POST is treated as nothing learned, so a migrated or
 * corrupted record can only ever lengthen the wait, never shorten it. */
static uint16_t host_boot_learned_value(void) {
    uint16_t stored=wake_store_last.host_boot_s;
    if(stored<HOST_BOOT_LEARN_MIN_S||stored>HOST_BOOT_LEARN_MAX_S) stored=0u;
    if(host_boot_learned_s>stored) stored=host_boot_learned_s;
    return stored;
}
static uint32_t power_button_grace_ms(void) {
    uint16_t learned=host_boot_learned_value();
    if(!learned) return POWER_BUTTON_GRACE_DEFAULT_MS;
    uint32_t grace=(uint32_t)learned*1000u+POWER_BUTTON_GRACE_MARGIN_MS;
    if(grace<POWER_BUTTON_GRACE_MIN_MS) grace=POWER_BUTTON_GRACE_MIN_MS;
    if(grace>POWER_BUTTON_GRACE_MAX_MS) grace=POWER_BUTTON_GRACE_MAX_MS;
    return grace;
}
/* The only number this machine has to teach the board is how long its own cold
 * start takes to put USB up, and the board learns it from the start it can
 * attribute: a start it did not press into.  A press moves the origin, so the
 * sample stays "time from the host's power-on to its USB", whichever way the host
 * was powered on. */
static void wake_learn_host_boot(uint32_t now) {
    if(host_boot_measured||!tud_mounted()||!host_boot_origin_at) return;
    host_boot_measured=true;
    uint32_t seconds=(now-host_boot_origin_at)/1000u;
    if(seconds<HOST_BOOT_LEARN_MIN_S||seconds>HOST_BOOT_LEARN_MAX_S) return;
    if(seconds<=(uint32_t)host_boot_learned_value()) return;
    host_boot_learned_s=(uint16_t)seconds;
    wake_store_dirty=true;
    printf("EVT|PWRBTN|host-boot|learned-s=%u|grace=%u\n",
           (unsigned)seconds,(unsigned)power_button_grace_ms());
}
/* The helper board's verdict on the machine's USB is the one input an operator
 * cannot see anywhere else, and it is what decides whether a press is safe: the
 * console is this board's own USB, so a log read after the fact has to carry it.
 * Every change is stamped with the clock the grace is measured against, which is
 * what makes a replay readable as a timeline. */
static uint8_t host_usb_logged_state=0xffu;
static void log_arm_host_state(uint32_t now) {
    if(!arm_uart_host_usb_seen()) return;
    uint8_t state=(uint8_t)arm_uart_host_usb_state();
    if(state==host_usb_logged_state) return;
    host_usb_logged_state=state;
    printf("EVT|HOST|usb=%s|mounted=%u|at-s=%u\n",
           state==ARM_HOST_USB_UP?"UP":(state==ARM_HOST_USB_SUSPEND?"SUSPEND":"DOWN"),
           tud_mounted()?1u:0u,(unsigned)(now/1000u));
}
static void service_wake_recovery(uint32_t now) {
    /* A host that came up on its own is this machine teaching the board how long
     * its cold start is, and that is the only number the button grace needs. */
    log_arm_host_state(now);
    wake_learn_host_boot(now);
    /* A clock acquisition ends the moment the sample it was pulsing for arrives:
     * the host that can send it is up by definition, and the deadline it re-arms
     * is the real one.  Checked before the phase dispatch so a board that only
     * needed the clock never spends a pulse on a host that already answered. */
    if(wake_recovery_reason==WAKE_RECOVERY_REASON_CLOCK&&wake_scheduler.synced) {
        wake_recovery_phase=WAKE_RECOVERY_IDLE;
        wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
        printf("EVT|WAKE|recovery|skipped|reason=clock\n");
        return;
    }
    if(wake_recovery_phase==WAKE_RECOVERY_PULSE) {
        if(!arm_uart_host_usb_seen()&&!pico_usb_suspended&&
           (int32_t)(now-wake_recovery_deadline)<0) return;
        if(wake_host_up()) {
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("EVT|WAKE|recovery|skipped|reason=host-up|arm-usb=%u|mounted=%u|at-s=%u\n",
                   (unsigned)arm_uart_host_usb_state(),tud_mounted()?1u:0u,(unsigned)(now/1000u));
            return;
        }
        if(calibration_runtime_active()) return;
        /* A powered-off PC takes the Arduino board down with it, so the
         * arm-readiness gate below must not swallow the one action that still
         * reaches that machine. */
        bool host_absent=POWER_BUTTON_ENABLED&&!tud_mounted();
        if(!host_absent&&(!arm_uart_mouse_ready()||arm_uart_mouse_busy())) {
            /* The normal wake path is blocked while the recovery runs, so a
             * board that cannot be reached must not hold the phase: give up
             * after the deadline instead of parking the wake machine. */
            if((int32_t)(now-wake_recovery_deadline)<0) return;
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("ERR|WAKE|recovery|skipped|reason=arm|ready=%u|busy=%u\n",
                   arm_uart_mouse_ready()?1u:0u,arm_uart_mouse_busy()?1u:0u);
            return;
        }
        /* A suspended bus reported by the Arduino board is a host that is present
         * and asleep: a press wakes that machine and must not be delayed.  A bus
         * that is neither mounted nor known asleep is a host that is off -- or one
         * that is still in POST, which reads exactly the same from here.  Only
         * that second reading is waited out, and the wait spends no attempt:
         * only a press does. */
        bool host_known_asleep=arm_uart_host_usb_seen()&&
                               arm_uart_host_usb_state()==ARM_HOST_USB_SUSPEND;
        if(host_absent&&!host_known_asleep) {
            uint32_t grace=power_button_grace_ms();
            if((int32_t)(now-power_button_boot_at)<(int32_t)grace) {
                if(!power_button_grace_logged) {
                    power_button_grace_logged=true;
                    printf("EVT|WAKE|recovery|skipped|reason=boot-grace|grace=%u|host-boot-s=%u|at-s=%u\n",
                           (unsigned)grace,(unsigned)host_boot_learned_value(),(unsigned)(now/1000u));
                }
                return;
            }
        }
        WakeStoreState state;
        if(!calibration_store_wake_get(&state)) {
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("ERR|WAKE|recovery|skipped|reason=store\n");
            return;
        }
        /* An owed wake is confirmed against the persisted decision it was
         * triggered from.  A clock acquisition has no deadline in that record --
         * that is exactly what a brownout mid-shift leaves behind -- so only the
         * shared pulse budget bounds it. */
        bool allowed=wake_recovery_reason==WAKE_RECOVERY_REASON_CLOCK
            ? wake_scheduler_pulse_budget_left(state.recovery_attempts,WAKE_RECOVERY_MAX_ATTEMPTS)
            : wake_scheduler_recovery_needed(state.host_asleep,state.pending,
                                             state.recovery_attempts,WAKE_RECOVERY_MAX_ATTEMPTS);
        if(!allowed) {
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("EVT|WAKE|recovery|skipped|reason=limit|attempts=%u\n",
                   (unsigned)state.recovery_attempts);
            return;
        }
        state.recovery_attempts=(uint8_t)(state.recovery_attempts+1u);
        if(!calibration_store_wake_set(&state)) {
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("ERR|WAKE|recovery|store\n");
            return;
        }
        wake_store_last=state;wake_store_dirty=false;
        wake_store_next_at=now+WAKE_STORE_MIN_INTERVAL_MS;
        /* Nothing is on our own bus at all, so the host is not suspended: it is
         * off.  No remote wake-up can reach a machine in soft-off, and the
         * Arduino board has no bus to drive either, so its own power button is
         * the only line left.  The attempt was persisted above, so this press
         * shares the one budget with the bus pulses and a brownout loop cannot
         * become a storm of button presses. */
        if(host_absent) {
            /* The host's cold start begins with this press, so whatever this boot
             * measures is measured from here and not from this board's own boot. */
            host_boot_origin_at=now;
            bool pressed=power_button_press(now,POWER_BUTTON_MS);
            wake_recovery_phase=WAKE_RECOVERY_WAIT;
            wake_recovery_deadline=now+WAKE_RECOVERY_TIMEOUT_MS;
            printf("EVT|WAKE|recovery=power-button|attempt=%u|ms=%u|pressed=%u|reason=%s\n",
                   (unsigned)state.recovery_attempts,(unsigned)POWER_BUTTON_MS,
                   pressed?1u:0u,wake_recovery_reason_text());
            if(!pressed) printf("ERR|PWRBTN|busy|reason=%s\n",wake_recovery_reason_text());
            return;
        }
        bool pico=wake_pulse_pico();
        ArmMouseSubmit arm_result=wake_pulse_arm(now);
        wake_recovery_phase=WAKE_RECOVERY_WAIT;
        wake_recovery_deadline=now+WAKE_RECOVERY_TIMEOUT_MS;
        char clock[16];
        printf("EVT|WAKE|recovery=pulse|attempt=%u|target=%02u:%02u|pico-rw=%u|arm=%u|usb=%u|wall=%s|reason=%s\n",
               (unsigned)state.recovery_attempts,
               state.next_start/60u,state.next_start%60u,pico?1u:0u,
               arm_result==ARM_MOUSE_ACCEPTED?1u:0u,
               (unsigned)arm_uart_host_usb_state(),wake_clock_text(clock,sizeof(clock)),
               wake_recovery_reason_text());
        if(!pico&&arm_result!=ARM_MOUSE_ACCEPTED)
            printf("ERR|WAKE|recovery=no-pulse|reason=%u\n",(unsigned)arm_result);
        return;
    }
    if(wake_recovery_phase==WAKE_RECOVERY_WAIT) {
        if(wake_host_up()) {
            wake_recovery_phase=WAKE_RECOVERY_IDLE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
            printf("EVT|WAKE|recovery=host-up|settled\n");
            return;
        }
        if((int32_t)(now-wake_recovery_deadline)<0) return;
        wake_recovery_phase=WAKE_RECOVERY_IDLE;
        wake_recovery_reason=WAKE_RECOVERY_REASON_NONE;
        buzzer_play(BUZZER_CUE_ERROR,now);
        printf("ERR|WAKE|recovery=no-resume|usb=%u\n",(unsigned)arm_uart_host_usb_state());
    }
}
static void service_wake(uint32_t now) {
    /* Recovery owns the wake path while it runs: the host is asleep and there is
     * no clock sample to re-arm from until the pulse brings the bridge back.  A
     * deadline that expires inside that window fires nothing at all, so say so
     * once, or a blocked wake is indistinguishable from a broken one. */
    if(wake_recovery_phase!=WAKE_RECOVERY_IDLE) {
        wake_report_blocked("recovery",now);
        service_wake_recovery(now);
        return;
    }
    /* Only a round that is actually driving the host competes with the wake: a
     * running or paused round keeps the machine awake by itself.  Every other
     * state must let the wake through, including `ABVM_STATUS_IDLE`, which is
     * what a freshly booted board sits in.  Gating on `STOPPED` silently disabled
     * the whole wake path after every flash until an operator pressed start/stop
     * once -- the wake fired no pulse and wrote no line, which is exactly how it
     * behaved in the field. */
    if(vm.status==ABVM_STATUS_RUNNING||vm.status==ABVM_STATUS_PAUSED) {
        wake_phase=WAKE_PHASE_IDLE;wake_pulse_inflight=false;
        wake_woke_host=false;wake_dismiss_done=false;wake_dismiss_step=0u;wake_dismiss_started=0u;
        wake_report_blocked("round",now);
        return;
    }
    if(wake_phase==WAKE_PHASE_PULSE) {
        if(wake_pulse_inflight&&(int32_t)(now-wake_deadline)<0)return;
        wake_pulse_inflight=false;wake_phase=WAKE_PHASE_RESUME;
        wake_deadline=now+WAKE_RESUME_TIMEOUT_MS;
        printf("EVT|WAKE|state=resume|wait-ms=%u\n",(unsigned)WAKE_RESUME_TIMEOUT_MS);
        return;
    }
    if(wake_phase==WAKE_PHASE_RESUME) {
        if(arm_uart_host_usb_state()==ARM_HOST_USB_UP) {
            wake_phase=WAKE_PHASE_SETTLE;wake_deadline=now+WAKE_SETTLE_MS;
            printf("EVT|WAKE|state=host-up|settle-ms=%u\n",(unsigned)WAKE_SETTLE_MS);
            return;
        }
        if((int32_t)(now-wake_deadline)<0)return;
        ++wake_attempts;
        wake_phase=WAKE_PHASE_IDLE;
        wake_retry_at=now+WAKE_RETRY_MS;
        buzzer_play(BUZZER_CUE_ERROR,now);
        printf("ERR|WAKE|no-resume|attempt=%u|usb=%u|retry-ms=%u\n",
               wake_attempts,(unsigned)arm_uart_host_usb_state(),(unsigned)WAKE_RETRY_MS);
        return;
    }
    if(wake_phase==WAKE_PHASE_SETTLE) {
        if((int32_t)(now-wake_deadline)<0)return;
#if WAKE_DISMISS_ENABLED
        /* The machine resumed on its lock screen; clear it before the authored
         * round takes over.  Only for a host this board actually woke. */
        if(wake_woke_host&&!wake_dismiss_done) {
            wake_phase=WAKE_PHASE_DISMISS;wake_dismiss_step=0u;wake_deadline=now;
            wake_dismiss_started=now;
            return;
        }
#endif
        wake_phase=WAKE_PHASE_IDLE;
        bool dry=wake_scheduler.dry;
        wake_scheduler_disarm(&wake_scheduler);
        /* A test arm is a test, not a schedule.  Give the deadline straight back
         * to the authored windows so one WAKE! run cannot leave the board unable
         * to wake for the real window.  A re-arm that does not happen must name
         * itself: a board left with no deadline writes no line at all, and that
         * silence is indistinguishable from a broken wake path. */
        if(wake_scheduler_rearm(&wake_scheduler,now))
            printf("EVT|WAKE|rearmed|window=%02u:%02u|in=%lu\n",
                   wake_scheduler.next_start/60u,wake_scheduler.next_start%60u,
                   (unsigned long)((wake_scheduler.deadline_ms-now)/60000u));
        else {
            char clock[16];
            const char *reason=!wake_scheduler.enabled?"schedule-off":
                               !wake_scheduler.synced?"no-clock":"inside-lead";
            printf("EVT|WAKE|rearm|skipped|reason=%s|lead=%u|wall=%s\n",
                   reason,wake_scheduler.lead_minutes,
                   wake_clock_text(clock,sizeof(clock)));
        }
        ++wake_attempts;
        if(dry) {
            /* WAKE!<seconds>!dry proves the resume and stops there: no authored
             * round may start while nobody is watching the desktop. */
            printf("EVT|WAKE|state=start|attempt=%u|skipped=manual-dry\n",wake_attempts);
            return;
        }
        printf("EVT|WAKE|state=start|attempt=%u\n",wake_attempts);
        start_control(now);
        if(!guard_runtime_running()&&vm.status!=ABVM_STATUS_RUNNING) {
            wake_retry_at=now+WAKE_RETRY_MS;
            printf("ERR|WAKE|start|retry-ms=%u\n",(unsigned)WAKE_RETRY_MS);
        }
        return;
    }
#if WAKE_DISMISS_ENABLED
    /* Paced Enter presses, each fire-and-forget with a fixed gap: the first
     * drops the lock screen, the next ones sign the machine in.  The phase can
     * never block the shift: it gives up after WAKE_DISMISS_TIMEOUT_MS, and a
     * refusal is logged and the sequence moves on. */
    if(wake_phase==WAKE_PHASE_DISMISS) {
        if((int32_t)(now-wake_deadline)<0)return;
        if((int32_t)(now-wake_dismiss_started-WAKE_DISMISS_TIMEOUT_MS)>=0) {
            printf("ERR|WAKE|dismiss|stall|step=%u|usb=%u\n",
                   (unsigned)wake_dismiss_step,pico_usb_suspended?1u:0u);
            wake_dismiss_step=WAKE_DISMISS_PRESSES;
        }
        if(wake_dismiss_step<WAKE_DISMISS_PRESSES) {
            HidKeyboardSubmit key=hid_keyboard_submit_trigger(WAKE_DISMISS_KEY,40u,90u,now);
            if(key==HID_KEYBOARD_BUSY) {
                /* Our own report is only held up while the host keeps this port
                 * suspended, which happens when it resumed through the Arduino
                 * board.  Ask for our port back, and let the board that
                 * certainly resumed press the key in the meantime. */
                if(!pico_usb_suspended) return;
                (void)wake_pulse_pico();
#if WAKE_DISMISS_ARM_FALLBACK
                if(!arm_uart_mouse_ready()||arm_uart_mouse_busy()) return;
                ArmMouseSubmit arm=arm_uart_mouse_submit_internal(WAKE_DISMISS_ARM_COMMAND,now);
                if(arm==ARM_MOUSE_BUSY) return;
                if(arm==ARM_MOUSE_ACCEPTED) {
                    printf("EVT|WAKE|state=dismiss|step=enter|n=%u|via=arm\n",
                           (unsigned)(wake_dismiss_step+1u));
                    wake_dismiss_step++;
                } else {
                    printf("ERR|WAKE|dismiss|arm=%u|n=%u\n",
                           (unsigned)arm,(unsigned)(wake_dismiss_step+1u));
                }
                wake_deadline=now+WAKE_DISMISS_GAP_MS;
#endif
                return;
            }
            if(key==HID_KEYBOARD_ACCEPTED)
                printf("EVT|WAKE|state=dismiss|step=enter|n=%u\n",(unsigned)(wake_dismiss_step+1u));
            else
                printf("ERR|WAKE|dismiss|key=%u|n=%u\n",
                       (unsigned)key,(unsigned)(wake_dismiss_step+1u));
            wake_dismiss_step++;
            wake_deadline=now+WAKE_DISMISS_GAP_MS;
            return;
        }
        wake_dismiss_step=0u;wake_dismiss_done=true;
        wake_phase=WAKE_PHASE_SETTLE;wake_deadline=now;
        return;
    }
#endif
    if(!wake_scheduler_armed(&wake_scheduler)||
       !wake_scheduler_due(&wake_scheduler,now)) { wake_block_logged=false; return; }
    if((int32_t)(now-wake_retry_at)<0) return;
    if(wake_attempts>=WAKE_MAX_ATTEMPTS) {
        wake_scheduler_disarm(&wake_scheduler);
        printf("ERR|WAKE|attempt-limit|attempts=%u\n",wake_attempts);
        return;
    }
    if(!wake_due_logged) {
        /* One line per deadline, naming every input the decision used, so a wake
         * that never left the board explains itself in the log. */
        wake_due_logged=true;
        printf("EVT|WAKE|due|manual=%u|dry=%u|attempts=%u|usb=%s|pico-usb=%u|pico-rw=%u\n",
               wake_scheduler.manual?1u:0u,wake_scheduler.dry?1u:0u,(unsigned)wake_attempts,
               cycle_host_name(arm_uart_host_usb_state()),
               pico_usb_suspended?1u:0u,pico_remote_wakeup_en?1u:0u);
    }
    if(calibration_runtime_active()) { wake_report_blocked("calibration",now); return; }
    if(arm_uart_host_usb_state()==ARM_HOST_USB_UP) {
        /* The host is already awake: start the authored round without a pulse and
         * without a lock-screen dismiss, because there is no lock screen to clear
         * and a stray click would land on whatever the operator is using.  That
         * verdict is a report and not a measurement, though, so confirm it over a
         * grace window that outlasts one report period: a machine that suspends
         * right at the deadline still gets its pulse. */
        if(!wake_host_awake_at) {
            wake_host_awake_at=now;
            printf("EVT|WAKE|due|host=up|confirm-ms=%u|pico-usb=%u\n",
                   (unsigned)WAKE_HOST_AWAKE_GRACE_MS,pico_usb_suspended?1u:0u);
            return;
        }
        if((int32_t)(now-wake_host_awake_at)<(int32_t)WAKE_HOST_AWAKE_GRACE_MS) return;
        wake_host_awake_at=0u;
        wake_woke_host=false;wake_dismiss_done=false;wake_dismiss_step=0u;wake_dismiss_started=0u;
        wake_phase=WAKE_PHASE_SETTLE;wake_deadline=now+WAKE_SETTLE_MS;
        printf("EVT|WAKE|state=host-awake|settle-ms=%u\n",(unsigned)WAKE_SETTLE_MS);
        return;
    }
    wake_host_awake_at=0u;
    /* Both wake sources are fired in the same pulse: they are armed by the host
     * independently, so whichever one Windows accepted is the one that resumes
     * the machine, and no latency is spent discovering which.  Neither source
     * may be gated on the other: the Pico resumes the host bus from its own
     * suspended port and needs nothing from the Arduino board, so an unprobed,
     * busy or faulted board must never be able to stop the machine from being
     * woken -- silently, which is how it used to behave. */
    bool pico=wake_pulse_pico();
    ArmMouseSubmit result=ARM_MOUSE_UNSUPPORTED;
    if(arm_uart_mouse_ready()&&!arm_uart_mouse_busy()) {
        result=wake_pulse_arm(now);
        if(!pico&&result==ARM_MOUSE_ACCEPTED)
            printf("ERR|WAKE|pulse|pico-skipped|usb=%u|rw=%u\n",
                   pico_usb_suspended?1u:0u,pico_remote_wakeup_en?1u:0u);
    } else if(!pico) {
        /* Nothing can go out yet.  Say why once, then keep trying: the deadline
         * stays due until a pulse actually leaves. */
        if(!wake_pulse_wait_logged) {
            wake_pulse_wait_logged=true;
            printf("ERR|WAKE|pulse|wait|arm-ready=%u|arm-busy=%u|pico-usb=%u|pico-rw=%u\n",
                   arm_uart_mouse_ready()?1u:0u,arm_uart_mouse_busy()?1u:0u,
                   pico_usb_suspended?1u:0u,pico_remote_wakeup_en?1u:0u);
        }
        return;
    } else {
        printf("ERR|WAKE|pulse|arm-skipped|ready=%u|busy=%u\n",
               arm_uart_mouse_ready()?1u:0u,arm_uart_mouse_busy()?1u:0u);
    }
    if(pico||result==ARM_MOUSE_ACCEPTED) {
        /* The Pico's own resume carries no acknowledgement, so the phase machine
         * only waits on the Arduino round trip when that path was used. */
        wake_pulse_wait_logged=false;
        wake_due_logged=false;wake_block_logged=false;
        wake_pulse_inflight=result==ARM_MOUSE_ACCEPTED;
        wake_woke_host=true;wake_dismiss_done=false;wake_dismiss_step=0u;
        wake_phase=WAKE_PHASE_PULSE;
        wake_deadline=now+WAKE_PULSE_TIMEOUT_MS;
        wake_report_pulse((uint8_t)(wake_attempts+1u),pico,result);
    } else if(result!=ARM_MOUSE_BUSY) {
        wake_scheduler_disarm(&wake_scheduler);
        printf("ERR|WAKE|pulse|reason=%u\n",(unsigned)result);
    }
}
void tud_umount_cb(void) {
    pico_usb_suspended=false;pico_remote_wakeup_en=false;
    shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u); release_all_actors(now_ms()); buzzer_silence();
}
void tud_suspend_cb(bool remote_wakeup_en) {
    pico_usb_suspended=true;pico_remote_wakeup_en=remote_wakeup_en;
    release_all_actors(now_ms()); buzzer_silence();
    /* `remote_wakeup_en` is the host's answer to SET_FEATURE(DEVICE_REMOTE_WAKEUP)
     * and it survives the resume, so STATUS reports the verdict an operator needs
     * before trusting the Pico-side wake path on this machine. */
    printf("EVT|USB|suspend|remote-wakeup=%u\n",remote_wakeup_en?1u:0u);
}
void tud_resume_cb(void) {
    pico_usb_suspended=false;
    printf("EVT|USB|resume|remote-wakeup=%u\n",pico_remote_wakeup_en?1u:0u);
}
static void configure_buzzer_cues(void){
    for(uint8_t cue_id=1u;cue_id<=23u;++cue_id){
        uint16_t hz[8],duration[8],gap[8];uint8_t count=0u,volume=100u,envelope=0u;
        BuzzerTone tones[8];
        if(!guard_runtime_buzzer_cue(cue_id,hz,duration,gap,&count,&volume,&envelope))continue;
        for(uint8_t i=0;i<count;++i)tones[i]=(BuzzerTone){hz[i],duration[i],gap[i]};
        buzzer_set_system_cue(cue_id,tones,count,volume,envelope);
    }
}
int main(void) {
    /* First statement, before the USB device or any actor exists: an optocoupler
     * across a PC's front-panel header is a real button press, so the line has to
     * be provably low from the earliest moment this board is powered. */
    power_button_init();
    status_led_init(now_ms());
    board_init(); hid_keyboard_init(); arm_uart_mouse_init(); light_sensor_init(now_ms()); buzzer_init();
    gpio_init(BUTTON_PAUSE_PIN); gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN); gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN); gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN); gpio_pull_up(BUTTON_START_STOP_PIN);
    const uint8_t *program = abvm_program_data(); size_t program_size = abvm_program_size();
    bool program_verified = abvm_init(&vm, program, program_size);
    bool guard_available = program_verified && guard_runtime_init(&vm);
    if(guard_available)configure_buzzer_cues();
    bool schedule_from_program = false;
    if (program_verified) {
        calibration_runtime_init(&vm);
        (void)cycle_runtime_init(&vm,now_ms());
        load_whisper_profile();
        ambient_mouse_init(now_ms());
        wake_scheduler_init(&wake_scheduler,WAKE_LEAD_MINUTES);
        /* The authored shift schedule travels inside the flashed program, so the
         * board can learn its windows at boot instead of waiting for a round to
         * reach its shift check.  Without this the board only ever learns the
         * schedule from a check it is supposed to trigger itself, which leaves a
         * freshly flashed board unable to arm the first window it must wake. */
        {
            const uint8_t *shift_payload;uint32_t shift_size;uint16_t shift_id;
            if(abvm_find_constant(&vm,ABVM_CONST_SHIFT,&shift_id,&shift_payload,&shift_size)&&
               shift_identity_load(&shift_identity,shift_payload,shift_size,
                                   now_ms()^local_u32(vm.header.program_sha256))) {
                wake_scheduler_configure(&wake_scheduler,shift_identity.schedule_enabled,
                                         shift_identity.day_start,shift_identity.day_end,
                                         shift_identity.night_start,shift_identity.night_end);
                schedule_from_program=true;
            }
        }
        /* A board reset loses the RAM deadline but not the decision behind it:
         * the persisted record still knows whether the host was asleep with a
         * wake owed, and that is enough to recover the clock.  A power cut loses
         * the anchor itself and leaves no decision at all, which is why the
         * second reason exists. */
        (void)calibration_store_wake_get(&wake_store_last);
        /* The button grace is counted from here: this is the moment at which a
         * silent bus can still be a machine in POST rather than a machine that is
         * off, and the moment the host's cold start begins when both powered up
         * together. */
        power_button_boot_at=now_ms();
        host_boot_origin_at=power_button_boot_at;
        host_boot_learned_s=0u;host_boot_measured=false;power_button_grace_logged=false;
        if(wake_scheduler_recovery_needed(wake_store_last.host_asleep,
                                          wake_store_last.pending,
                                          wake_store_last.recovery_attempts,
                                          WAKE_RECOVERY_MAX_ATTEMPTS)) {
            wake_recovery_phase=WAKE_RECOVERY_PULSE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_OWED;
            wake_recovery_deadline=now_ms()+WAKE_HOST_SAMPLE_TIMEOUT_MS;
        } else if(wake_scheduler_boot_clock_needed(wake_scheduler.enabled,
                                                   wake_scheduler.synced,
                                                   wake_store_last.recovery_attempts,
                                                   WAKE_RECOVERY_MAX_ATTEMPTS)) {
            /* Power came back and the RAM anchor is gone with it.  The schedule
             * survived inside the flashed program, so the windows are known, but
             * no window can be armed without a clock sample -- and the only
             * source of that sample is a host that may be asleep.  Bring it up
             * once so its bridge can hand the clock over; the same persisted
             * budget bounds the pulses, so a brownout loop still cannot turn into
             * a wake storm. */
            wake_recovery_phase=WAKE_RECOVERY_PULSE;
            wake_recovery_reason=WAKE_RECOVERY_REASON_CLOCK;
            wake_recovery_deadline=now_ms()+WAKE_HOST_SAMPLE_TIMEOUT_MS;
        }
    }
    /* Do not expose a half-ready USB device while a large patched ABP image is
     * being hashed and structurally verified. Attach only after boot work. */
    tusb_init();
    /* A board that reset while the host was asleep can never finish enumerating:
     * the port stays suspended until something resumes it, so an unbounded wait
     * would hang the recovery pulse forever.  The same is true of a host that is
     * simply off, and there the board has to stay alive on its own: a machine that
     * is off is exactly the machine whose power button it may have to press, and a
     * board parked in this loop never reaches that press -- or anything else.  The
     * wait is therefore bounded whatever the recovery thinks, and the lines held
     * back during it are replayed from RAM when a console appears, so a host that
     * enumerates later still sees the whole boot. */
    uint32_t mount_started=now_ms();
    while (!tud_mounted()) {
        tud_task(); sleep_ms(1);
        if((int32_t)(now_ms()-(mount_started+WAKE_MOUNT_TIMEOUT_MS))>=0) break;
    }
    printf("EVT|USB|mount|at-s=%u\n",(unsigned)(now_ms()/1000u));
    if (!arm_uart_mouse_probe(now_ms())) arm_fault_reported = true;
    if (!program_verified) {
        while (true) { tud_task(); printf("ERR|ABVM|boot-verify|reason=%s\n", vm.fault ? vm.fault : "unknown"); sleep_ms(1000); }
    }
    printf("BOOT|ABVM|format=%u|abi=%u|bytes=%lu|state-bytes=%lu|frames=%u|lanes=%u|interrupts=%u|hid=keyboard+type+arm-rmouse|light=bh1750|guard=%u|cycle=%u|buzzer=legacy-calibration-gp6\n", ABVM_FORMAT_VERSION, ABVM_VM_ABI, (unsigned long)program_size, (unsigned long)sizeof(vm), vm.resources.max_frames, vm.resources.max_lanes, vm.resources.max_interrupts, guard_available, cycle_runtime_available());
    printf("READY|keys=GP3-pause-long-soundcal,GP4-guard-long-lightcal|arm=UART0-GP16-GP17-57600|buzzer=GP6-legacy-calibration-nonblocking|pwrbtn=GP%u-momentary%s|cdc=PING,STATUS,SETRES,WSND,BEEP,BEEPSEQ,LUX?,LCAL-ms,SCAL-ms,GUARD-ON-OFF,PAUSE,RESUME,WHISPER,WHISPER-REPEAT,SOUND-id,TIME!HH:MM,WAKE!s-WAKE!s!dry-WAKE!OFF,WAKE?,PWRBTN-ms,LED!0-999\n",
           (unsigned)POWER_BUTTON_PIN,POWER_BUTTON_ENABLED?"":"-disabled");
    if(schedule_from_program)
        printf("EVT|WAKE|schedule|source=program|enabled=%u|day=%02u:%02u-%02u:%02u|night=%02u:%02u-%02u:%02u|lead=%u\n",
               wake_scheduler.enabled?1u:0u,
               wake_scheduler.day_start/60u,wake_scheduler.day_start%60u,
               wake_scheduler.day_end/60u,wake_scheduler.day_end%60u,
               wake_scheduler.night_start/60u,wake_scheduler.night_start%60u,
               wake_scheduler.night_end/60u,wake_scheduler.night_end%60u,
               wake_scheduler.lead_minutes);
    else
        printf("EVT|WAKE|schedule|source=none|enabled=0\n");
    if (wake_recovery_phase!=WAKE_RECOVERY_IDLE)
        printf("EVT|WAKE|recovery|armed|reason=%s|target=%02u:%02u|attempts=%u\n",
               wake_recovery_reason_text(),
               wake_store_last.next_start/60u,wake_store_last.next_start%60u,
               (unsigned)wake_store_last.recovery_attempts);
    else if (wake_scheduler.enabled&&!wake_scheduler.synced)
        /* A schedule that needs a clock, no clock, and no pulse budget left to
         * fetch one with.  Say so: a board that will never arm its first window
         * otherwise looks exactly like a healthy one. */
        printf("EVT|WAKE|recovery|skipped|reason=limit|attempts=%u\n",
               (unsigned)wake_store_last.recovery_attempts);
    while (true) { uint32_t now = now_ms(); tud_task(); service_cdc(now); service_power_button(now); service_status_led(now); service_buttons(now); service_keyboard(now); service_mouse(now); service_cycle(now); guard_runtime_set_input_locked(input_lock_active()); service_light(now); service_buzzer_action(now); service_shift_check(now); service_wake(now); wake_store_service(now,false); service_game_buffs(now); service_vm(now); service_ambient_mouse(now); service_pending_sound_whisper(now); service_global_sound_listener(now); buzzer_service(now); sleep_ms(1); }
}
