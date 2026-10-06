/* Host harness for the portable human-mouse engine.  It loops the ARM side of
 * the UART link in software: every frame the Pico code emits is checksum-
 * verified and acknowledged instantly, and every MMOVE step is logged as
 * "M|<now>|<dx>|<dy>" so test_abvm_human_mouse_motion.py can measure the
 * motion signature (tick sizes, flicks, tremor, joins, warm-up) without
 * hardware. */
#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "abvm_abi1.h"
#include "abvm_vm.h"
#include "arm_uart_mouse.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- fake board UART ---------------------------------------------------- */
static char tx_line[128];
static unsigned tx_len;
static char rx_queue[512];
static unsigned rx_head, rx_tail;
static uint32_t harness_now;
static FILE *log_out;

uart_inst_t uart0_storage;
uart_inst_t *const uart0 = &uart0_storage;

uint uart_init(uart_inst_t *uart, uint baudrate) {
    (void)uart; return baudrate;
}
void uart_set_format(uart_inst_t *uart, uint data_bits, uint stop_bits,
                     uart_parity_t parity) {
    (void)uart; (void)data_bits; (void)stop_bits; (void)parity;
}
void uart_set_fifo_enabled(uart_inst_t *uart, bool enabled) {
    (void)uart; (void)enabled;
}
void gpio_set_function(uint pin, uint function) { (void)pin; (void)function; }
bool calibration_store_sound_get(uint16_t profile_id, uint16_t *threshold,
                                 uint16_t *minimum_ms) {
    (void)profile_id; (void)threshold; (void)minimum_ms; return false;
}

static void rx_push(const char *text) {
    while (*text) {
        if (rx_tail + 1u >= sizeof(rx_queue)) { fprintf(stderr, "rx overflow\n"); exit(1); }
        rx_queue[rx_tail++] = *text++;
    }
}
bool uart_is_readable(uart_inst_t *uart) {
    (void)uart;
    if (rx_head == rx_tail) rx_head = rx_tail = 0;
    return rx_head < rx_tail;
}
char uart_getc(uart_inst_t *uart) {
    (void)uart;
    char c = rx_queue[rx_head++];
    if (rx_head == rx_tail) rx_head = rx_tail = 0;
    return c;
}
bool uart_is_writable(uart_inst_t *uart) { (void)uart; return true; }

static void arm_answer(const char *payload) {
    if (!strcmp(payload, "HVER")) { rx_push("OK|HVER|harness|REL=1|ASND=1\n"); return; }
    if (!strncmp(payload, "MMOVE|", 6u)) {
        long dx = 0, dy = 0;
        if (sscanf(payload + 6, "%ld,%ld", &dx, &dy) == 2)
            fprintf(log_out, "M|%lu|%ld|%ld\n",
                    (unsigned long)harness_now, dx, dy);
        rx_push("OK|MMOVE\n"); return;
    }
    if (!strncmp(payload, "ASND|", 5u)) { rx_push("OK|ASND\n"); return; }
    if (!strcmp(payload, "ASNDCANCEL")) { rx_push("OK|ASNDCANCEL\n"); return; }
    if (!strcmp(payload, "HALT")) { rx_push("OK|HALT\n"); return; }
    rx_push("ERR|NOFRAME\n");
}
void uart_putc_raw(uart_inst_t *uart, char c) {
    (void)uart;
    if (c == '\n') {
        tx_line[tx_len] = 0;
        if (tx_line[0] == '#') {
            char *sep = strchr(tx_line, '|');
            unsigned want = 0;
            if (!sep || sscanf(tx_line + 1, "%2x", &want) != 1) {
                fprintf(stderr, "bad frame: %s\n", tx_line); exit(1);
            }
            unsigned sum = 0;
            for (const char *p = sep + 1; *p; ++p) sum = (sum + (uint8_t)*p) & 0xffu;
            if (sum != want) { fprintf(stderr, "checksum: %s\n", tx_line); exit(1); }
            arm_answer(sep + 1);
        }
        tx_len = 0;
        return;
    }
    if (tx_len + 1u >= sizeof(tx_line)) { fprintf(stderr, "tx overflow\n"); exit(1); }
    tx_line[tx_len++] = c;
}

/* ---- driver ------------------------------------------------------------- */
static int require(int condition, const char *message) {
    if (!condition) fprintf(stderr, "human mouse smoke failure: %s\n", message);
    return condition;
}

static void run_until_idle(AbvmVm *vm, uint32_t *now, uint32_t budget_ms) {
    (void)vm;
    uint32_t deadline = *now + budget_ms;
    uint8_t completed = 0;
    while (*now < deadline) {
        harness_now = *now;
        arm_uart_mouse_service(*now, &completed);
        if (!arm_uart_mouse_busy()) break;
        ++*now;
    }
    harness_now = *now;
}

int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: abvm_human_mouse_smoke program.abp out.log\n");
        return 2;
    }
    log_out = fopen(argv[2], "w");
    if (!log_out) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    fseek(file, 0, SEEK_END); long length = ftell(file); rewind(file);
    uint8_t *image = malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length) return 2;
    fclose(file);
    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "image")) return 1;
    arm_uart_mouse_init();
    uint32_t now = 100;
    harness_now = now;
    if (!require(arm_uart_mouse_probe(now), "probe")) return 1;
    run_until_idle(&vm, &now, 2000u);
    if (!require(arm_uart_mouse_ready(), "arm ready")) return 1;

    /* submit every MOUSE constant in the image, in slot order */
    unsigned moves = 0;
    for (uint16_t id = 0; id < 256u; ++id) {
        const uint8_t *payload; uint32_t size;
        if (!abvm_constant(&vm, id, ABVM_CONST_MOUSE, &payload, &size))
            continue;
        AbvmEvent event;
        memset(&event, 0, sizeof(event));
        event.opcode = ABVM_OP_RMOUSE;
        event.operand_a = id;
        event.lane = 1u;
        now += 80u;
        if (!require(arm_uart_mouse_submit(&vm, &event, now) ==
                     ARM_MOUSE_ACCEPTED, "submit accepted")) return 1;
        fprintf(log_out, "BEGIN|%u|%lu\n", id, (unsigned long)now);
        uint32_t start = now;
        run_until_idle(&vm, &now, 20000u);
        fprintf(log_out, "END|%lu\n", (unsigned long)now);
        if (!require(!arm_uart_mouse_faulted(),
                     arm_uart_mouse_fault())) return 1;
        (void)start;
        ++moves;
        /* every third move follows a long idle to exercise warm-up */
        now += (moves % 3u == 0u) ? 4000u : 300u;
    }
    if (!require(moves >= 4u, "fixture exposes several mouse constants")) return 1;
    fclose(log_out);
    printf("human mouse smoke: %u moves\n", moves);
    return 0;
}
