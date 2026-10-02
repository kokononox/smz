
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "bsp/board.h"
#include "pico/stdlib.h"
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

extern const uint8_t *abvm_program_data(void);
extern size_t abvm_program_size(void);
#define BUTTON_PAUSE_PIN 3u
#define BUTTON_START_STOP_PIN 4u
#define BUTTON_DEBOUNCE_MS 30u
#define BUTTON_LONG_MS 3000u
#define GAME_ROUTE_ID 8u
#define WHISPER_ROUTE_ID 10u
#define WHISPER_REPEAT_ROUTE_ID 12u

typedef struct Button { uint pin; bool raw, stable, long_sent, consumed; uint32_t changed_at, pressed_at; } Button;
typedef enum ButtonEvent { BUTTON_NONE, BUTTON_DOWN, BUTTON_SHORT, BUTTON_LONG } ButtonEvent;
static AbvmVm vm;
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
    return hid_keyboard_locked()||arm_uart_mouse_busy();
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
    if(!ambient_mouse_available)return;
    if(ambient_mouse_inflight) {
        if(!guard_runtime_running()||guard_runtime_paused()||
           guard_runtime_watchdog_tripped()||
           cycle_runtime_restart_critical()||calibration_runtime_active()||
           light_sensor_calibration_active())
            arm_uart_mouse_release_all(now);
        return;
    }
    if(!guard_runtime_running()||guard_runtime_paused()||
       guard_runtime_watchdog_tripped()||cycle_runtime_restart_critical()||
       calibration_runtime_active()||light_sensor_calibration_active()||
       vm.status==ABVM_STATUS_RUNNING||vm.status==ABVM_STATUS_PAUSED||
       input_lock_active()||
       (int32_t)(now-ambient_mouse_next_due)<0)
        return;
    uint8_t profile=guard_runtime_active_profile();
    if(!profile||profile>8u||
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
static int cdc_printf(const char *format, ...) {
    char output[384]; va_list args; va_start(args, format);
    int length = vsnprintf(output, sizeof(output), format, args); va_end(args);
    if (length <= 0 || !tud_cdc_connected()) return length;
    size_t count = (size_t)length;
    if (count >= sizeof(output)) count = sizeof(output) - 1u;
    tud_cdc_write(output, (uint32_t)count); tud_cdc_write_flush(); return length;
}
#define printf cdc_printf
static void print_status(void) {
    printf("STATUS|state=%s|route=%u|lanes=%u|pc0=%lu|pc1=%lu|frames0=%u|frames1=%u|suspended=%u|hid-busy=%u|sound-active=%u|light-present=%u|light-watch=%u|light-cal=%u|guard=%u|guard-paused=%u|guard-profile=%s|guard-stage=%u|cycle=%u|time=%lu\n", abvm_status_name(vm.status), vm.route_id, vm.lane_count, (unsigned long)vm.lanes[0].pc, (unsigned long)vm.lanes[1].pc, vm.lanes[0].frame_count, vm.lanes[1].frame_count, vm.suspended.valid, hid_keyboard_busy() || arm_uart_mouse_busy(), arm_uart_sound_active(), light_sensor_present(), light_sensor_watch_active(), light_sensor_calibration_active(), guard_runtime_running(), guard_runtime_paused(), guard_runtime_profile_name(guard_runtime_active_profile()), guard_runtime_stage(), cycle_runtime_available(), (unsigned long)vm.now);
}
static void release_all_actors(uint32_t now) {
    hid_keyboard_release_all(); arm_uart_mouse_release_all(now);
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
    if (calibration_runtime_active()) { printf("ERR|GUARD|CALIBRATING\n"); return; }
    if (!arm_uart_mouse_ready()) {
        printf("ERR|GUARD|ARM|ready=0|version=%s|detail=%s\n", arm_uart_mouse_version(), arm_uart_mouse_fault());
        return;
    }
    if (guard_runtime_available()) {
        if (!light_sensor_present()) { printf("ERR|GUARD|NOSENSOR\n"); return; }
        abvm_stop(&vm, now);
        if (guard_runtime_start(now)) {
            cycle_runtime_manual_start(now);
            printf("OK|GUARD|ON\n"); buzzer_play(BUZZER_CUE_START, now);
        }
        else printf("ERR|GUARD|START\n");
    } else if (abvm_start_route(&vm, GAME_ROUTE_ID, now))
        printf("CONTROL|start|route=Game|guard=unavailable\n");
    else printf("ERR|CONTROL|start\n");
}
static void stop_control(uint32_t now) {
    buzzer_watchdog_alarm_stop();
    guard_runtime_stop(); abvm_stop(&vm, now); release_all_actors(now);
    pending_sound_whisper=false;
    ui_sound_watch_pending=false;ui_buzzer_reply_pending=false;
    ui_buzzer_sequence_reply=false;
    cycle_runtime_manual_stop();
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
        if (abvm_resume(&vm, now)) { printf("CONTROL|resume\n"); buzzer_play(BUZZER_CUE_RESUME, now); } else printf("ERR|CONTROL|resume\n");
    } else if (vm.status == ABVM_STATUS_RUNNING) {
        if (abvm_pause(&vm, now)) { printf("CONTROL|pause\n"); buzzer_play(BUZZER_CUE_PAUSE, now); } else printf("ERR|CONTROL|pause\n");
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
    if (!strcmp(line, "PING")) printf("OK|PONG|combined-pico-guard-executor|native=abvm|abi=%u|format=%u|hid=on|uart=on|arm-ready=%u|arm-usb=%u|arm-ver=%s|profiles=%u|buzzer=legacy-calibration-gp6|role=brain\n", ABVM_VM_ABI, ABVM_FORMAT_VERSION, arm_uart_mouse_ready(), arm_uart_host_usb_state(), arm_uart_mouse_version(), guard_runtime_available() ? 8u : 0u);
    else if (!strcmp(line, "STATUS")) print_status();
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
    if (hid_keyboard_service(now, &lane) &&
        !abvm_complete_action(&vm, lane, now))
        printf("ERR|HID|complete|lane=%u\n", lane);
    char reply[24];
    if(hid_keyboard_take_live_reply(reply,sizeof(reply)))printf("%s\n",reply);
}
static bool light_cal_cue_active, sound_cal_cue_active;
static uint8_t event_u8(const char *event,const char *key,uint8_t fallback) {
    const char *p=strstr(event,key); if(!p)return fallback;
    unsigned long value=strtoul(p+strlen(key),NULL,10);
    return value>255u?fallback:(uint8_t)value;
}
static void service_calibration_cue(const char *event,uint32_t now) {
    bool sound=strstr(event,"|SOUNDCAL|")!=NULL;
    bool error=!strncmp(event,"ERR|",4);
    uint8_t selection=event_u8(event,sound?"id=":"stage=",1u);
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
        else if(!sound&&selection==8u)
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
        if(arm_uart_mouse_internal_completion(completed_lane))
            ambient_mouse_complete(now);
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
                printf("EVT|CYCLE|startup-start|route=%u|cycle=%u\n",
                       event.route_id,event.count);break;
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
    CycleAction action=cycle_runtime_service(now,true,host);
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
    }
}
static void service_vm(uint32_t now) {
    if (arm_uart_mouse_releasing()) {
        return;
    }
    AbvmEvent event = abvm_tick(&vm, now);
    switch (event.type) {
        case ABVM_EVENT_ACTION: { ArmMouseSubmit mouse = arm_uart_mouse_submit(&vm, &event, now);
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
void tud_umount_cb(void) { release_all_actors(now_ms()); buzzer_silence(); }
void tud_suspend_cb(bool remote_wakeup_en) {
    (void)remote_wakeup_en; release_all_actors(now_ms()); buzzer_silence();
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
    board_init(); hid_keyboard_init(); arm_uart_mouse_init(); light_sensor_init(now_ms()); buzzer_init();
    gpio_init(BUTTON_PAUSE_PIN); gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN); gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN); gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN); gpio_pull_up(BUTTON_START_STOP_PIN);
    const uint8_t *program = abvm_program_data(); size_t program_size = abvm_program_size();
    bool program_verified = abvm_init(&vm, program, program_size);
    bool guard_available = program_verified && guard_runtime_init(&vm);
    if(guard_available)configure_buzzer_cues();
    if (program_verified) {
        calibration_runtime_init(&vm);
        (void)cycle_runtime_init(&vm,now_ms());
        load_whisper_profile();
        ambient_mouse_init(now_ms());
    }
    /* Do not expose a half-ready USB device while a large patched ABP image is
     * being hashed and structurally verified. Attach only after boot work. */
    tusb_init();
    while (!tud_mounted()) { tud_task(); sleep_ms(1); }
    if (!arm_uart_mouse_probe(now_ms())) arm_fault_reported = true;
    if (!program_verified) {
        while (true) { tud_task(); printf("ERR|ABVM|boot-verify|reason=%s\n", vm.fault ? vm.fault : "unknown"); sleep_ms(1000); }
    }
    printf("BOOT|ABVM|format=%u|abi=%u|bytes=%lu|state-bytes=%lu|frames=%u|lanes=%u|interrupts=%u|hid=keyboard+type+arm-rmouse|light=bh1750|guard=%u|cycle=%u|buzzer=legacy-calibration-gp6\n", ABVM_FORMAT_VERSION, ABVM_VM_ABI, (unsigned long)program_size, (unsigned long)sizeof(vm), vm.resources.max_frames, vm.resources.max_lanes, vm.resources.max_interrupts, guard_available, cycle_runtime_available());
    printf("READY|keys=GP3-pause-long-soundcal,GP4-guard-long-lightcal|arm=UART0-GP16-GP17-57600|buzzer=GP6-legacy-calibration-nonblocking|cdc=PING,STATUS,SETRES,WSND,BEEP,BEEPSEQ,LUX?,LCAL-ms,SCAL-ms,GUARD-ON-OFF,PAUSE,RESUME,WHISPER,WHISPER-REPEAT,SOUND-id\n");
    while (true) { uint32_t now = now_ms(); tud_task(); service_cdc(now); service_buttons(now); service_keyboard(now); service_mouse(now); service_cycle(now); guard_runtime_set_input_locked(input_lock_active()); service_light(now); service_buzzer_action(now); service_vm(now); service_ambient_mouse(now); service_pending_sound_whisper(now); service_global_sound_listener(now); buzzer_service(now); sleep_ms(1); }
}
