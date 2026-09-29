#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "pico/stdlib.h"

#include "abvm_vm.h"

extern const uint8_t abvm_program_image[];
extern const size_t abvm_program_image_size;

#define BUTTON_PAUSE_PIN 3u
#define BUTTON_START_STOP_PIN 4u
#define BUTTON_DEBOUNCE_MS 30u
#define GAME_ROUTE_ID 8u
#define WHISPER_ROUTE_ID 10u

typedef struct Button {
    uint pin;
    bool raw;
    bool stable;
    uint32_t changed_at;
} Button;

static AbvmVm vm;
static Button pause_button = {BUTTON_PAUSE_PIN, false, false, 0};
static Button start_button = {BUTTON_START_STOP_PIN, false, false, 0};
static char command[96];
static size_t command_length;

static uint32_t now_ms(void) {
    return to_ms_since_boot(get_absolute_time());
}

static void print_status(void) {
    printf("STATUS|state=%s|route=%u|lanes=%u|pc0=%lu|pc1=%lu|"
           "frames0=%u|frames1=%u|suspended=%u|time=%lu\n",
           abvm_status_name(vm.status), vm.route_id, vm.lane_count,
           (unsigned long)vm.lanes[0].pc, (unsigned long)vm.lanes[1].pc,
           vm.lanes[0].frame_count, vm.lanes[1].frame_count,
           vm.suspended.valid, (unsigned long)vm.now);
}

static void start_game(uint32_t now) {
    if (abvm_start_route(&vm, GAME_ROUTE_ID, now))
        printf("CONTROL|start|route=Game\n");
    else
        printf("ERR|CONTROL|start\n");
}

static void toggle_pause(uint32_t now) {
    if (vm.status == ABVM_STATUS_PAUSED) {
        if (abvm_resume(&vm, now)) printf("CONTROL|resume\n");
        else printf("ERR|CONTROL|resume\n");
    } else if (vm.status == ABVM_STATUS_RUNNING) {
        if (abvm_pause(&vm, now)) printf("CONTROL|pause\n");
        else printf("ERR|CONTROL|pause\n");
    } else {
        printf("CONTROL|pause-ignored|state=%s\n",
               abvm_status_name(vm.status));
    }
}

static void toggle_start_stop(uint32_t now) {
    if (vm.status == ABVM_STATUS_RUNNING ||
        vm.status == ABVM_STATUS_PAUSED) {
        abvm_stop(&vm, now);
        printf("CONTROL|stop\n");
    } else {
        start_game(now);
    }
}

static bool button_pressed(Button *button, uint32_t now) {
    bool raw = !gpio_get(button->pin);
    if (raw != button->raw) {
        button->raw = raw;
        button->changed_at = now;
    }
    if (raw != button->stable &&
        (uint32_t)(now - button->changed_at) >= BUTTON_DEBOUNCE_MS) {
        button->stable = raw;
        return raw;
    }
    return false;
}

static void service_buttons(uint32_t now) {
    if (button_pressed(&pause_button, now)) toggle_pause(now);
    if (button_pressed(&start_button, now)) toggle_start_stop(now);
}

static void execute_command(char *line, uint32_t now) {
    if (!strcmp(line, "PING")) {
        printf("OK|PONG|abvm-native-pico|abi=%u|format=%u|role=brain\n",
               ABVM_VM_ABI, ABVM_FORMAT_VERSION);
    } else if (!strcmp(line, "STATUS")) {
        print_status();
    } else if (!strcmp(line, "START")) {
        start_game(now);
    } else if (!strcmp(line, "PAUSE")) {
        if (!abvm_pause(&vm, now)) printf("ERR|CONTROL|pause\n");
    } else if (!strcmp(line, "RESUME")) {
        if (!abvm_resume(&vm, now)) printf("ERR|CONTROL|resume\n");
    } else if (!strcmp(line, "STOP")) {
        abvm_stop(&vm, now);
        printf("CONTROL|stop\n");
    } else if (!strcmp(line, "WHISPER")) {
        if (abvm_interrupt_route(&vm, WHISPER_ROUTE_ID, now))
            printf("CONTROL|interrupt|route=Whisper\n");
        else
            printf("ERR|CONTROL|interrupt\n");
    } else if (!strncmp(line, "SOUND ", 6)) {
        uint16_t profile = (uint16_t)strtoul(line + 6, NULL, 10);
        printf("%s|SOUND|profile=%u\n",
               abvm_sound_detected(&vm, profile, now) ? "OK" : "MISS",
               profile);
    } else if (*line) {
        printf("ERR|COMMAND|unknown=%s\n", line);
    }
}

static void service_cdc(uint32_t now) {
    int value;
    while ((value = getchar_timeout_us(0)) != PICO_ERROR_TIMEOUT) {
        if (value == '\r' || value == '\n') {
            if (command_length) {
                command[command_length] = 0;
                execute_command(command, now);
                command_length = 0;
            }
        } else if (value >= 32 && value <= 126) {
            if (command_length + 1u < sizeof(command))
                command[command_length++] = (char)value;
            else
                command_length = 0;
        }
    }
}

static void service_vm(uint32_t now) {
    AbvmEvent event = abvm_tick(&vm, now);
    switch (event.type) {
        case ABVM_EVENT_ACTION:
            /*
             * Hardware actors are intentionally not enabled in this bring-up
             * UF2. The event boundary is nonblocking and already carries every
             * operand required by the future HID/UART adapters.
             */
            printf("ACTION|stub|lane=%u|op=%u|a=%u|b=%lu|c=%lu|d=%lu\n",
                   event.lane, event.opcode, event.operand_a,
                   (unsigned long)event.operand_b,
                   (unsigned long)event.operand_c,
                   (unsigned long)event.operand_d);
            if (!abvm_complete_action(&vm, event.lane, now))
                printf("ERR|ACTION|complete\n");
            break;
        case ABVM_EVENT_WATCH_ARMED:
            printf("WATCH|armed|lane=%u|profile=%u|timeout=%lu,%lu\n",
                   event.lane, event.operand_a,
                   (unsigned long)event.operand_b,
                   (unsigned long)event.operand_c);
            break;
        case ABVM_EVENT_RELEASE_ALL:
            printf("HID|release-all|stub\n");
            break;
        case ABVM_EVENT_INTERRUPT_RESUME:
            printf("CONTROL|interrupt-resume|route=%u\n", vm.route_id);
            break;
        case ABVM_EVENT_ROUTE_COMPLETE:
            printf("ROUTE|complete|route=%u\n", event.route_id);
            break;
        case ABVM_EVENT_FAULT:
            printf("ERR|ABVM|%s\n", event.message ? event.message : "fault");
            break;
        default:
            break;
    }
}

int main(void) {
    stdio_init_all();
    gpio_init(BUTTON_PAUSE_PIN);
    gpio_set_dir(BUTTON_PAUSE_PIN, GPIO_IN);
    gpio_pull_up(BUTTON_PAUSE_PIN);
    gpio_init(BUTTON_START_STOP_PIN);
    gpio_set_dir(BUTTON_START_STOP_PIN, GPIO_IN);
    gpio_pull_up(BUTTON_START_STOP_PIN);

    sleep_ms(800);
    if (!abvm_init(&vm, abvm_program_image, abvm_program_image_size)) {
        printf("ERR|ABVM|boot-verify|reason=%s\n",
               vm.fault ? vm.fault : "unknown");
        while (true) {
            service_cdc(now_ms());
            sleep_ms(10);
        }
    }

    printf("BOOT|ABVM|format=%u|abi=%u|bytes=%lu|state-bytes=%lu|"
           "frames=%u|lanes=%u|interrupts=%u\n",
           ABVM_FORMAT_VERSION, ABVM_VM_ABI,
           (unsigned long)abvm_program_image_size,
           (unsigned long)sizeof(vm), vm.resources.max_frames,
           vm.resources.max_lanes, vm.resources.max_interrupts);
    printf("READY|keys=GP3-pause,GP4-start-stop|cdc=PING,STATUS,START,"
           "PAUSE,RESUME,STOP,WHISPER,SOUND-id\n");

    while (true) {
        uint32_t now = now_ms();
        service_cdc(now);
        service_buttons(now);
        service_vm(now);
        sleep_ms(1);
    }
}