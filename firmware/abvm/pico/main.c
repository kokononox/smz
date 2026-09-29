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

extern const uint8_t *abvm_program_data(void);
extern size_t abvm_program_size(void);
#define BUTTON_PAUSE_PIN 3u
#define BUTTON_START_STOP_PIN 4u
#define BUTTON_DEBOUNCE_MS 30u
#define GAME_ROUTE_ID 8u
#define WHISPER_ROUTE_ID 10u

typedef struct Button { uint pin; bool raw, stable; uint32_t changed_at; } Button;
static AbvmVm vm;
static Button pause_button = {BUTTON_PAUSE_PIN, false, false, 0};
static Button start_button = {BUTTON_START_STOP_PIN, false, false, 0};
static char command[96];
static size_t command_length;
static uint32_t now_ms(void) { return to_ms_since_boot(get_absolute_time()); }
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
    printf("STATUS|state=%s|route=%u|lanes=%u|pc0=%lu|pc1=%lu|frames0=%u|frames1=%u|suspended=%u|hid-busy=%u|sound-active=%u|light-present=%u|light-watch=%u|light-cal=%u|guard=%u|guard-paused=%u|guard-profile=%s|guard-stage=%u|time=%lu\n", abvm_status_name(vm.status), vm.route_id, vm.lane_count, (unsigned long)vm.lanes[0].pc, (unsigned long)vm.lanes[1].pc, vm.lanes[0].frame_count, vm.lanes[1].frame_count, vm.suspended.valid, hid_keyboard_busy() || arm_uart_mouse_busy(), arm_uart_sound_active(), light_sensor_present(), light_sensor_watch_active(), light_sensor_calibration_active(), guard_runtime_running(), guard_runtime_paused(), guard_runtime_profile_name(guard_runtime_active_profile()), guard_runtime_stage(), (unsigned long)vm.now);
}
static void release_all_actors(uint32_t now) { hid_keyboard_release_all(); arm_uart_mouse_release_all(now); light_sensor_cancel_watch(now); }
static void start_control(uint32_t now) {
    if (guard_runtime_available()) {
        if (!light_sensor_present()) { printf("ERR|GUARD|NOSENSOR\n"); return; }
        abvm_stop(&vm, now);
        if (guard_runtime_start(now)) printf("OK|GUARD|ON\n");
        else printf("ERR|GUARD|START\n");
    } else if (abvm_start_route(&vm, GAME_ROUTE_ID, now))
        printf("CONTROL|start|route=Game|guard=unavailable\n");
    else printf("ERR|CONTROL|start\n");
}
static void stop_control(uint32_t now) {
    guard_runtime_stop(); abvm_stop(&vm, now); printf("OK|GUARD|OFF\n");
}
static void toggle_pause(uint32_t now) {
    if (guard_runtime_running()) {
        if (guard_runtime_paused()) {
            bool vm_ok = vm.status != ABVM_STATUS_PAUSED || abvm_resume(&vm, now);
            if (guard_runtime_resume() && vm_ok) printf("CONTROL|resume|guard=on\n");
            else printf("ERR|CONTROL|resume\n");
        } else {
            bool vm_ok = vm.status != ABVM_STATUS_RUNNING || abvm_pause(&vm, now);
            if (guard_runtime_pause() && vm_ok) printf("CONTROL|pause|guard=on\n");
            else printf("ERR|CONTROL|pause\n");
        }
    } else if (vm.status == ABVM_STATUS_PAUSED) {
        if (abvm_resume(&vm, now)) printf("CONTROL|resume\n"); else printf("ERR|CONTROL|resume\n");
    } else if (vm.status == ABVM_STATUS_RUNNING) {
        if (abvm_pause(&vm, now)) printf("CONTROL|pause\n"); else printf("ERR|CONTROL|pause\n");
    } else printf("CONTROL|pause-ignored|state=%s\n", abvm_status_name(vm.status));
}
static void toggle_start_stop(uint32_t now) {
    if (guard_runtime_running() || vm.status == ABVM_STATUS_RUNNING ||
        vm.status == ABVM_STATUS_PAUSED) stop_control(now);
    else start_control(now);
}
static bool button_pressed(Button *button, uint32_t now) {
    bool raw = !gpio_get(button->pin); if (raw != button->raw) { button->raw = raw; button->changed_at = now; }
    if (raw != button->stable && (uint32_t)(now - button->changed_at) >= BUTTON_DEBOUNCE_MS) { button->stable = raw; return raw; }
    return false;
}
static void service_buttons(uint32_t now) { if (button_pressed(&pause_button, now)) toggle_pause(now); if (button_pressed(&start_button, now)) toggle_start_stop(now); }
static void execute_command(char *line, uint32_t now) {
    if (!strcmp(line, "PING")) printf("OK|PONG|combined-pico-guard-executor|native=abvm|abi=%u|format=%u|hid=on|uart=on|profiles=%u|role=brain\n", ABVM_VM_ABI, ABVM_FORMAT_VERSION, guard_runtime_available() ? 6u : 0u);
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
    else if (!strcmp(line, "START") || !strcmp(line, "GUARD|ON")) start_control(now);
    else if (!strcmp(line, "PAUSE")) {
        bool vm_ok = vm.status != ABVM_STATUS_RUNNING || abvm_pause(&vm, now);
        bool guard_ok = !guard_runtime_running() || guard_runtime_pause();
        if (vm_ok && guard_ok) printf("CONTROL|pause|guard=%u\n", guard_runtime_running());
        else printf("ERR|CONTROL|pause\n");
    }
    else if (!strcmp(line, "RESUME")) {
        bool vm_ok = vm.status != ABVM_STATUS_PAUSED || abvm_resume(&vm, now);
        bool guard_ok = !guard_runtime_running() || guard_runtime_resume();
        if (vm_ok && guard_ok) printf("CONTROL|resume|guard=%u\n", guard_runtime_running());
        else printf("ERR|CONTROL|resume\n");
    }
    else if (!strcmp(line, "STOP") || !strcmp(line, "GUARD|OFF") ||
             !strcmp(line, "HALT") || !strcmp(line, "HALT|SILENT")) stop_control(now);
    else if (!strcmp(line, "CALSTATUS"))
        printf("OK|CALSTATUS|revision=native-abp|source=abp|count=%u|last_error=none\n", guard_runtime_available() ? 6u : 0u);
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
static void service_light(uint32_t now) {
    light_sensor_service(&vm, now);
    if (light_sensor_take_fault()) {
        printf("ERR|LIGHT|sensor-lost\n");
        guard_runtime_stop(); abvm_stop(&vm, now);
    }
    if (!light_sensor_calibration_active()) guard_runtime_service(&vm, now);
    GuardRuntimeEvent guard_event;
    if (guard_runtime_take_event(&guard_event)) {
        const char *profile = guard_runtime_profile_name(guard_event.profile_id);
        if (guard_event.type == GUARD_EVENT_ROUTE)
            printf("EVT|GUARD|route=%u|profile=%s|stage=%u|context=%u|lux=%lu.%lu|reason=%s\n", guard_event.route_id, profile, guard_event.stage, guard_event.context, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason);
        else if (guard_event.type == GUARD_EVENT_FAULT)
            printf("ERR|GUARD|%s\n", guard_event.reason);
        else
            printf("EVT|GUARD|state=%s|stage=%u|lux=%lu.%lu|reason=%s\n", profile, guard_event.stage, (unsigned long)(guard_event.lux_tenths / 10u), (unsigned long)(guard_event.lux_tenths % 10u), guard_event.reason);
    }
    LightCalibrationResult result;
    if (light_sensor_calibration_take(&result)) {
        if (result.valid)
            printf("OK|LCAL|min=%lu|max=%lu|avg=%lu|samples=%lu\n", (unsigned long)result.minimum_lux, (unsigned long)result.maximum_lux, (unsigned long)result.average_lux, (unsigned long)result.samples);
        else printf("ERR|NOSENSOR|LCAL\n");
    }
}
static void service_mouse(uint32_t now) {
    uint8_t completed_lane;
    if (arm_uart_mouse_service(now, &completed_lane) && (vm.status == ABVM_STATUS_RUNNING || vm.status == ABVM_STATUS_PAUSED) && !abvm_complete_action(&vm, completed_lane, now)) printf("ERR|ARM|complete|lane=%u\n", completed_lane);
    uint16_t profile, peak; bool detected;
    if (arm_uart_sound_take(&profile, &detected, &peak)) {
        if (detected) {
            bool accepted = abvm_sound_detected(&vm, profile, now);
            printf("%s|SOUND|profile=%u|peak=%u|source=arm\n", accepted ? "OK" : "MISS", profile, peak);
        } else printf("SOUND|timeout|profile=%u|peak=%u|source=arm\n", profile, peak);
    }
    if (arm_uart_mouse_faulted() && vm.status != ABVM_STATUS_STOPPED && vm.status != ABVM_STATUS_FAULT) { printf("ERR|ARM|%s\n", arm_uart_mouse_fault()); abvm_stop(&vm, now); }
}
static void service_vm(uint32_t now) {
    if (arm_uart_mouse_releasing()) {
        return;
    }
    AbvmEvent event = abvm_tick(&vm, now);
    switch (event.type) {
        case ABVM_EVENT_ACTION: { ArmMouseSubmit mouse = arm_uart_mouse_submit(&vm, &event, now);
            if (mouse == ARM_MOUSE_ACCEPTED) { printf("ARM|mouse|accepted|lane=%u\n", event.lane); break; }
            if (mouse != ARM_MOUSE_UNSUPPORTED) { printf("ERR|ARM|submit|lane=%u|reason=%u\n", event.lane, mouse); abvm_stop(&vm, now); break; }
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
            if (sound == ARM_SOUND_ACCEPTED) printf("WATCH|armed|lane=%u|profile=%u|timeout=%lu|source=arm\n", event.lane, event.operand_a, (unsigned long)event.operand_b);
            else { printf("ERR|ARM|sound-arm|lane=%u|profile=%u|reason=%u\n", event.lane, event.operand_a, sound); abvm_stop(&vm, now); }
            break;
        }
        case ABVM_EVENT_RELEASE_ALL: release_all_actors(now); printf("HID|release-all|queued\n"); break;
        case ABVM_EVENT_INTERRUPT_RESUME: printf("CONTROL|interrupt-resume|route=%u\n", vm.route_id); break;
        case ABVM_EVENT_ROUTE_COMPLETE: release_all_actors(now); printf("ROUTE|complete|route=%u\n", event.route_id); break;
        case ABVM_EVENT_FAULT: release_all_actors(now); printf("ERR|ABVM|%s\n", event.message ? event.message : "fault"); break;
        default: break;
    }
}
void tud_umount_cb(void) { release_all_actors(now_ms()); }
void tud_suspend_cb(bool remote_wakeup_en) { (void)remote_wakeup_en; release_all_actors(now_ms()); }
int main(void) {
    board_init(); tusb_init(); hid_keyboard_init(); arm_uart_mouse_init(); light_sensor_init(now_ms());
    gpio_init(BUTTON_PAUSE_PIN); gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN); gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN); gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN); gpio_pull_up(BUTTON_START_STOP_PIN);
    const uint8_t *program = abvm_program_data(); size_t program_size = abvm_program_size();
    if (!abvm_init(&vm, program, program_size)) { while (true) { tud_task(); printf("ERR|ABVM|boot-verify|reason=%s\n", vm.fault ? vm.fault : "unknown"); sleep_ms(1000); } }
    bool guard_available = guard_runtime_init(&vm);
    while (!tud_mounted()) { tud_task(); sleep_ms(1); }
    printf("BOOT|ABVM|format=%u|abi=%u|bytes=%lu|state-bytes=%lu|frames=%u|lanes=%u|interrupts=%u|hid=keyboard+type+arm-rmouse|light=bh1750|guard=%u\n", ABVM_FORMAT_VERSION, ABVM_VM_ABI, (unsigned long)program_size, (unsigned long)sizeof(vm), vm.resources.max_frames, vm.resources.max_lanes, vm.resources.max_interrupts, guard_available);
    printf("READY|keys=GP3-pause,GP4-guard-start-stop|arm=UART0-GP16-GP17-57600|cdc=PING,STATUS,LUX?,LCAL-ms,GUARD-ON-OFF,PAUSE,RESUME,WHISPER,SOUND-id\n");
    while (true) { uint32_t now = now_ms(); tud_task(); service_cdc(now); service_buttons(now); service_keyboard(now); service_mouse(now); service_light(now); service_vm(now); sleep_ms(1); }
}
