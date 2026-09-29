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
    printf("STATUS|state=%s|route=%u|lanes=%u|pc0=%lu|pc1=%lu|frames0=%u|frames1=%u|suspended=%u|hid-busy=%u|time=%lu\n", abvm_status_name(vm.status), vm.route_id, vm.lane_count, (unsigned long)vm.lanes[0].pc, (unsigned long)vm.lanes[1].pc, vm.lanes[0].frame_count, vm.lanes[1].frame_count, vm.suspended.valid, hid_keyboard_busy() || arm_uart_mouse_busy(), (unsigned long)vm.now);
}
static void release_all_actors(uint32_t now) { hid_keyboard_release_all(); arm_uart_mouse_release_all(now); }
static void start_game(uint32_t now) { if (abvm_start_route(&vm, GAME_ROUTE_ID, now)) printf("CONTROL|start|route=Game\n"); else printf("ERR|CONTROL|start\n"); }
static void toggle_pause(uint32_t now) {
    if (vm.status == ABVM_STATUS_PAUSED) { if (abvm_resume(&vm, now)) printf("CONTROL|resume\n"); else printf("ERR|CONTROL|resume\n"); }
    else if (vm.status == ABVM_STATUS_RUNNING) { if (abvm_pause(&vm, now)) printf("CONTROL|pause\n"); else printf("ERR|CONTROL|pause\n"); }
    else printf("CONTROL|pause-ignored|state=%s\n", abvm_status_name(vm.status));
}
static void toggle_start_stop(uint32_t now) { if (vm.status == ABVM_STATUS_RUNNING || vm.status == ABVM_STATUS_PAUSED) { abvm_stop(&vm, now); printf("CONTROL|stop\n"); } else start_game(now); }
static bool button_pressed(Button *button, uint32_t now) {
    bool raw = !gpio_get(button->pin); if (raw != button->raw) { button->raw = raw; button->changed_at = now; }
    if (raw != button->stable && (uint32_t)(now - button->changed_at) >= BUTTON_DEBOUNCE_MS) { button->stable = raw; return raw; }
    return false;
}
static void service_buttons(uint32_t now) { if (button_pressed(&pause_button, now)) toggle_pause(now); if (button_pressed(&start_button, now)) toggle_start_stop(now); }
static void execute_command(char *line, uint32_t now) {
    if (!strcmp(line, "PING")) printf("OK|PONG|abvm-native-pico|abi=%u|format=%u|hid=keyboard+type+arm-rmouse|role=brain\n", ABVM_VM_ABI, ABVM_FORMAT_VERSION);
    else if (!strcmp(line, "STATUS")) print_status(); else if (!strcmp(line, "START")) start_game(now);
    else if (!strcmp(line, "PAUSE")) { if (!abvm_pause(&vm, now)) printf("ERR|CONTROL|pause\n"); }
    else if (!strcmp(line, "RESUME")) { if (!abvm_resume(&vm, now)) printf("ERR|CONTROL|resume\n"); }
    else if (!strcmp(line, "STOP")) { abvm_stop(&vm, now); printf("CONTROL|stop\n"); }
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
static void service_mouse(uint32_t now) {
    uint8_t completed_lane;
    if (arm_uart_mouse_service(now, &completed_lane) && (vm.status == ABVM_STATUS_RUNNING || vm.status == ABVM_STATUS_PAUSED) && !abvm_complete_action(&vm, completed_lane, now)) printf("ERR|ARM|complete|lane=%u\n", completed_lane);
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
        case ABVM_EVENT_WATCH_ARMED: printf("WATCH|armed|lane=%u|profile=%u|timeout=%lu,%lu\n", event.lane, event.operand_a, (unsigned long)event.operand_b, (unsigned long)event.operand_c); break;
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
    board_init(); tusb_init(); hid_keyboard_init(); arm_uart_mouse_init();
    gpio_init(BUTTON_PAUSE_PIN); gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN); gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN); gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN); gpio_pull_up(BUTTON_START_STOP_PIN);
    const uint8_t *program = abvm_program_data(); size_t program_size = abvm_program_size();
    if (!abvm_init(&vm, program, program_size)) { while (true) { tud_task(); printf("ERR|ABVM|boot-verify|reason=%s\n", vm.fault ? vm.fault : "unknown"); sleep_ms(1000); } }
    while (!tud_mounted()) { tud_task(); sleep_ms(1); }
    printf("BOOT|ABVM|format=%u|abi=%u|bytes=%lu|state-bytes=%lu|frames=%u|lanes=%u|interrupts=%u|hid=keyboard+type+arm-rmouse\n", ABVM_FORMAT_VERSION, ABVM_VM_ABI, (unsigned long)program_size, (unsigned long)sizeof(vm), vm.resources.max_frames, vm.resources.max_lanes, vm.resources.max_interrupts);
    printf("READY|keys=GP3-pause,GP4-start-stop|arm=UART0-GP16-GP17-57600|cdc=PING,STATUS,START,PAUSE,RESUME,STOP,WHISPER,SOUND-id\n");
    while (true) { uint32_t now = now_ms(); tud_task(); service_cdc(now); service_buttons(now); service_keyboard(now); service_mouse(now); service_vm(now); sleep_ms(1); }
}
