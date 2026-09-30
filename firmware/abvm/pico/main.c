
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

typedef struct Button { uint pin; bool raw, stable, long_sent, consumed; uint32_t changed_at, pressed_at; } Button;
typedef enum ButtonEvent { BUTTON_NONE, BUTTON_DOWN, BUTTON_SHORT, BUTTON_LONG } ButtonEvent;
static AbvmVm vm;
static Button pause_button = {.pin=BUTTON_PAUSE_PIN};
static Button start_button = {.pin=BUTTON_START_STOP_PIN};
static char command[96];
static size_t command_length;
static bool arm_fault_reported;
static bool ui_sound_calibration_pending;
static uint32_t ui_sound_calibration_deadline;
static bool buzzer_action_pending;
static uint8_t buzzer_action_lane;
static uint32_t buzzer_action_deadline;
static bool ui_sound_watch_pending;
static bool ui_buzzer_reply_pending;
static uint32_t ui_buzzer_reply_deadline;
static bool whisper_profile_enabled;
static uint16_t whisper_threshold;
static uint16_t whisper_minimum;
static bool whisper_rearm_pending;
static uint16_t whisper_rearm_profile;
static uint16_t whisper_rearm_threshold;
static uint16_t whisper_rearm_minimum;
static uint32_t whisper_rearm_deadline;
static uint32_t now_ms(void) { return to_ms_since_boot(get_absolute_time()); }
static uint16_t local_u16(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}
static uint32_t local_u32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static void load_whisper_profile(void) {
    const uint8_t *payload; uint32_t size; uint16_t constant_id;
    whisper_profile_enabled=false;
    if (!abvm_find_constant(&vm,ABVM_CONST_SOUND,&constant_id,&payload,&size) ||
        size!=8u || local_u16(payload)!=1u) return;
    whisper_threshold=local_u16(payload+2u);
    whisper_minimum=(uint16_t)local_u32(payload+4u);
    if (!whisper_threshold &&
        !calibration_store_sound_get(1u,&whisper_threshold,&whisper_minimum))
        return;
    whisper_profile_enabled=whisper_threshold>0u && whisper_threshold<=1023u &&
                            whisper_minimum>0u;
}
static uint32_t sound_watch_remaining(uint16_t profile,uint32_t now) {
    for (uint8_t i=0;i<ABVM_MAX_LANES;++i) {
        const AbvmLane *lane=&vm.lanes[i];
        if (lane->active && lane->blocked==ABVM_BLOCK_WATCH &&
            lane->watch_kind==ABVM_CONST_SOUND &&
            lane->watch_profile==profile) {
            if ((int32_t)(lane->watch_deadline-now)<=0) return 0u;
            return lane->watch_deadline-now;
        }
    }
    return 0u;
}
static void service_whisper_rearm(uint32_t now) {
    if (!whisper_rearm_pending) return;
    if ((int32_t)(now-whisper_rearm_deadline)>=0) {
        whisper_rearm_pending=false; return;
    }
    if (vm.status!=ABVM_STATUS_RUNNING || vm.route_id!=GAME_ROUTE_ID ||
        arm_uart_sound_active()) return;
    uint32_t remaining=whisper_rearm_deadline-now;
    if (arm_uart_sound_restart(now,whisper_rearm_profile,
                               whisper_rearm_threshold,
                               whisper_rearm_minimum,remaining))
        whisper_rearm_pending=false;
}
static int cdc_printf(const char *format, ...) {
    char output[256]; va_list args; va_start(args, format);
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
    ui_buzzer_reply_pending=false;
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
    guard_runtime_stop(); abvm_stop(&vm, now); release_all_actors(now);
    ui_sound_watch_pending=false;ui_buzzer_reply_pending=false;
    cycle_runtime_manual_stop();
    printf("OK|GUARD|OFF\n"); buzzer_play(BUZZER_CUE_STOP, now);
}
static void toggle_pause(uint32_t now) {
    if (guard_runtime_running()) {
        if (guard_runtime_paused()) {
            bool vm_ok = vm.status != ABVM_STATUS_PAUSED || abvm_resume(&vm, now);
            if (guard_runtime_resume() && vm_ok) { printf("CONTROL|resume|guard=on\n"); buzzer_play(BUZZER_CUE_RESUME, now); }
            else printf("ERR|CONTROL|resume\n");
        } else {
            bool vm_ok = vm.status != ABVM_STATUS_RUNNING || abvm_pause(&vm, now);
            if (guard_runtime_pause() && vm_ok) { printf("CONTROL|pause|guard=on\n"); buzzer_play(BUZZER_CUE_PAUSE, now); }
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
    if (!strcmp(line, "PING")) printf("OK|PONG|combined-pico-guard-executor|native=abvm|abi=%u|format=%u|hid=on|uart=on|arm-ready=%u|arm-ver=%s|profiles=%u|buzzer=legacy-calibration-gp6|role=brain\n", ABVM_VM_ABI, ABVM_FORMAT_VERSION, arm_uart_mouse_ready(), arm_uart_mouse_version(), guard_runtime_available() ? 6u : 0u);
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
    else if (!strncmp(line, "MMOVE|", 6)) {
        /* Live Classroom preview is intentionally write-only: bridge.py
         * streams and paces the path, then synchronizes with PING. */
        ArmMouseSubmit result=arm_uart_mouse_submit_live(line,now);
        if(result!=ARM_MOUSE_ACCEPTED)
            printf("ERR|MMOVE|reason=%u|arm-ready=%u|arm-usb=%u\n",
                   result,arm_uart_mouse_ready(),arm_uart_host_usb_state());
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
                else ui_sound_watch_pending=true;
            }
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
            ui_buzzer_reply_pending=true;
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
        printf("OK|CALSTATUS|revision=%lu|source=nvm-a-b|count=%u|mode=%u|last_error=none\n", (unsigned long)calibration_store_revision(), guard_runtime_available() ? 6u : 0u, calibration_runtime_mode());
    else if (!strcmp(line, "WHISPER")) { if (abvm_interrupt_route(&vm, WHISPER_ROUTE_ID, now)) printf("CONTROL|interrupt|route=Whisper\n"); else printf("ERR|CONTROL|interrupt\n"); }
    else if (!strncmp(line, "SOUND ", 6)) { uint16_t profile = (uint16_t)strtoul(line + 6, NULL, 10); printf("%s|SOUND|profile=%u\n", abvm_sound_detected(&vm, profile, now) ? "OK" : "MISS", profile); }
    else if (*line) printf("ERR|COMMAND|unknown=%s\n", line);
}
static void service_cdc(uint32_t now) {
    while (tud_cdc_available()) { int value = tud_cdc_read_char();
        if (value == '\r' || value == '\n') { if (command_length) { command[command_length] = 0; execute_command(command, now); command_length = 0; } }
        else if (value >= 32 && value <= 126) { if (command_length + 1u < sizeof(command)) command[command_length++] = (char)value; else command_length = 0; }
    }
}
static void service_keyboard(uint32_t now) { uint8_t lane; if (hid_keyboard_service(now, &lane) && !abvm_complete_action(&vm, lane, now)) printf("ERR|HID|complete|lane=%u\n", lane); }
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
    if(error){buzzer_calibration_save_error(now);return;}
    if(strstr(event,"mode=exited")){buzzer_calibration_exit(now);if(sound)sound_cal_cue_active=false;else light_cal_cue_active=false;return;}
    if(strstr(event,"mode=ready")){bool *seen=sound?&sound_cal_cue_active:&light_cal_cue_active;if(!*seen){*seen=true;buzzer_calibration_enter(selection,sound,now);}else buzzer_calibration_position(selection,sound,now);return;}
    if(strstr(event,"mode=started")||strstr(event,"mode=silence")){buzzer_calibration_record_start(sound,now);return;}
    if(sound&&strstr(event,"mode=sound")){buzzer_calibration_sound_target(now);return;}
    if(!sound&&strstr(event,"mode=complete")){buzzer_calibration_stage_complete(selection,now);return;}
    if(strstr(event,"mode=saved")){if(!sound&&selection==6u)buzzer_calibration_complete(now);else buzzer_calibration_save_success(now);}
}
static void service_light(uint32_t now) {
    light_sensor_service(&vm, now);
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
        if (guard_event.type == GUARD_EVENT_ROUTE) {
            if (strcmp(guard_event.reason,"start-at-current-state")) {
                buzzer_guard_transition(guard_event.profile_id, now);
                printf("BUZZER|cue=transition|profile=%s|stage=%u\n",
                       profile,guard_event.stage);
            }
            printf("EVT|GUARD|route=%u|profile=%s|stage=%u|context=%u|lux=%lu.%lu|reason=%s\n", guard_event.route_id, profile, guard_event.stage, guard_event.context, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason);
        } else if (guard_event.type == GUARD_EVENT_FAULT) {
            buzzer_play(BUZZER_CUE_ERROR, now);
            cycle_runtime_fail(6u);
            printf("ERR|GUARD|%s\n", guard_event.reason);
        }
        else
            printf("EVT|GUARD|state=%s|stage=%u|lux=%lu.%lu|reason=%s\n", profile, guard_event.stage, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason);
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
    if (arm_uart_mouse_service(now, &completed_lane) && (vm.status == ABVM_STATUS_RUNNING || vm.status == ABVM_STATUS_PAUSED) && !abvm_complete_action(&vm, completed_lane, now)) printf("ERR|ARM|complete|lane=%u\n", completed_lane);
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
            if(detected)printf("OK|WSND|DETECTED|peak=%u\n",peak);
            else printf("ERR|TIMEOUT|WSND|max=%u\n",peak);
            return;
        }
        if (detected) {
            uint32_t remaining=sound_watch_remaining(profile,now);
            if (whisper_profile_enabled && vm.route_id==GAME_ROUTE_ID &&
                peak>=whisper_threshold && remaining &&
                abvm_interrupt_route(&vm,WHISPER_ROUTE_ID,now)) {
                whisper_rearm_pending=true;
                whisper_rearm_profile=profile;
                whisper_rearm_threshold=arm_uart_sound_threshold();
                whisper_rearm_minimum=arm_uart_sound_minimum();
                whisper_rearm_deadline=now+remaining;
                printf("CONTROL|interrupt|route=Whisper|source=sound|profile=%u|peak=%u|threshold=%u\n",
                       profile,peak,whisper_threshold);
                return;
            }
            bool accepted = abvm_sound_detected(&vm, profile, now);
            printf("%s|SOUND|profile=%u|peak=%u|threshold=%u|min=%u|config=%s|source=arm\n",
                   accepted ? "OK" : "MISS",profile,peak,
                   arm_uart_sound_threshold(),arm_uart_sound_minimum(),
                   arm_uart_sound_uses_calibration()?"saved":"project");
        } else {
            printf("SOUND|timeout|profile=%u|peak=%u|threshold=%u|min=%u|config=%s|source=arm\n",
                   profile,peak,arm_uart_sound_threshold(),
                   arm_uart_sound_minimum(),
                   arm_uart_sound_uses_calibration()?"saved":"project");
            buzzer_play(BUZZER_CUE_TIMEOUT, now);
        }
    }
    service_whisper_rearm(now);
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
        ui_buzzer_reply_pending=false;printf("OK|BEEP\n");
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
                printf("EVT|CYCLE|armed-at-boot|count=%u\n",event.count);break;
            case CYCLE_EVENT_DEADLINE:
                printf("EVT|CYCLE|deadline|action=after\n");break;
            case CYCLE_EVENT_AFTER_START:
                printf("EVT|CYCLE|after-start|route=%u|count=%u\n",
                       event.route_id,event.count);break;
            case CYCLE_EVENT_AFTER_COMPLETE:
                printf("EVT|CYCLE|after-complete|wait=usb-restart|down-seen=%u\n",
                       event.down_seen);break;
            case CYCLE_EVENT_USB:
                printf("EVT|CYCLE|usb|state=%s\n",
                       cycle_host_name(event.host_state));break;
            case CYCLE_EVENT_STARTUP_START:
                printf("EVT|CYCLE|startup-start|route=%u\n",event.route_id);break;
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
                printf("EVT|CYCLE|startup-complete|next=login-or-dc|desktop=skip\n");
                if(!guard_runtime_start_after_restart(now))cycle_runtime_fail(3u);
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
int main(void) {
    board_init(); hid_keyboard_init(); arm_uart_mouse_init(); light_sensor_init(now_ms()); buzzer_init();
    gpio_init(BUTTON_PAUSE_PIN); gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN); gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN); gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN); gpio_pull_up(BUTTON_START_STOP_PIN);
    const uint8_t *program = abvm_program_data(); size_t program_size = abvm_program_size();
    bool program_verified = abvm_init(&vm, program, program_size);
    bool guard_available = program_verified && guard_runtime_init(&vm);
    if (program_verified) {
        calibration_runtime_init(&vm);
        (void)cycle_runtime_init(&vm,now_ms());
        load_whisper_profile();
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
    printf("READY|keys=GP3-pause-long-soundcal,GP4-guard-long-lightcal|arm=UART0-GP16-GP17-57600|buzzer=GP6-legacy-calibration-nonblocking|cdc=PING,STATUS,SETRES,WSND,BEEP,LUX?,LCAL-ms,SCAL-ms,GUARD-ON-OFF,PAUSE,RESUME,WHISPER,SOUND-id\n");
    while (true) { uint32_t now = now_ms(); tud_task(); service_cdc(now); service_buttons(now); service_keyboard(now); service_mouse(now); service_cycle(now); service_light(now); service_buzzer_action(now); service_vm(now); buzzer_service(now); sleep_ms(1); }
}
