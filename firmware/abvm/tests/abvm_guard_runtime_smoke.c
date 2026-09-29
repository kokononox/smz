#include "abvm_vm.h"
#include "guard_runtime.h"
#include <stdio.h>
#include <stdlib.h>

bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now) {
    (void)lux_tenths; (void)age_ms; (void)now; return false;
}
static int require(int condition, const char *message) {
    if (!condition) fprintf(stderr, "ABVM Guard smoke failure: %s\n", message);
    return condition;
}
static int stable(AbvmVm *vm, uint32_t lux, uint32_t at, uint16_t route,
                  uint8_t stage) {
    guard_runtime_observe(vm, lux, at);
    guard_runtime_observe(vm, lux, at + 100u);
    GuardRuntimeEvent event;
    if (!require(guard_runtime_take_event(&event), "Guard event") ||
        !require(event.type == GUARD_EVENT_ROUTE, "route event") ||
        !require(event.route_id == route, "route id") ||
        !require(event.stage == stage, "stage") ||
        !require(vm->route_id == route, "VM route")) return 0;
    return 1;
}
int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *file = fopen(argv[1], "rb"); if (!file) return 2;
    fseek(file, 0, SEEK_END); long length = ftell(file); rewind(file);
    uint8_t *image = malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length) return 2;
    fclose(file);
    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "image") ||
        !require(guard_runtime_init(&vm), "descriptor") ||
        !require(guard_runtime_available(), "available") ||
        !require(guard_runtime_start(0u), "start")) return 1;
    if (!stable(&vm, 1000u, 0u, 1u, 1u)) return 1;
    if (!require(guard_runtime_pause() && guard_runtime_paused(), "pause") ||
        !require(guard_runtime_resume() && !guard_runtime_paused(), "resume")) return 1;
    guard_runtime_observe(&vm, 1060u, 150u);
    if (!require(guard_runtime_active_profile() == 1u, "desktop hysteresis")) return 1;
    if (!stable(&vm, 2000u, 200u, 4u, 2u) ||
        !stable(&vm, 3000u, 400u, 6u, 3u) ||
        !stable(&vm, 4000u, 600u, 7u, 4u) ||
        !stable(&vm, 5000u, 800u, 8u, 5u) ||
        !stable(&vm, 6000u, 1000u, 9u, 5u)) return 1;
    guard_runtime_observe(&vm, 5000u, 1200u);
    guard_runtime_observe(&vm, 5000u, 1300u);
    GuardRuntimeEvent event;
    if (!require(guard_runtime_take_event(&event), "targeted return") ||
        !require(event.type == GUARD_EVENT_STATE && event.route_id == 0u,
                 "Game does not replay after Targeted")) return 1;
    if (!stable(&vm, 2000u, 1400u, 5u, 2u)) return 1;
    if (!require(guard_runtime_active_profile() == 2u, "DC profile") ||
        !require(guard_runtime_stage() == 2u, "DC resets stage")) return 1;
    guard_runtime_stop();
    if (!require(!guard_runtime_running(), "stop")) return 1;
    free(image);
    puts("ABVM native global Guard state machine smoke passed");
    return 0;
}
