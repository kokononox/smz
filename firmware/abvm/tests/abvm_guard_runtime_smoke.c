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
        !stable(&vm, 5000u, 800u, 8u, 5u)) return 1;
    /*
     * Simulate a sound/manual-origin Whisper: unlike the optical path this
     * does not set Guard's light-interrupt marker.  Route identity alone must
     * still make the overlay exclusive until all of its steps finish.
     */
    if (!require(abvm_interrupt_route(&vm,10u,950u),
                 "non-light Whisper interrupt")) return 1;
    guard_runtime_observe(&vm,1000u,1000u);
    guard_runtime_observe(&vm,1000u,1100u);
    GuardRuntimeEvent event;
    if (!require(vm.route_id==10u&&vm.suspended.valid,
                 "Desktop light cannot preempt sound/manual Whisper") ||
        !require(!guard_runtime_take_event(&event),
                 "non-light Whisper suppresses optical events")) return 1;
    vm.route_id=8u;vm.suspended.valid=false;
    if (!stable(&vm, 7000u, 1200u, 10u, 5u)) return 1;
    if (!require(vm.suspended.valid, "light Whisper interrupts Game")) return 1;
    guard_runtime_observe(&vm,1000u,1300u);
    guard_runtime_observe(&vm,1000u,1400u);
    if (!require(vm.route_id==10u&&vm.suspended.valid,
                 "Desktop light cannot preempt Whisper New") ||
        !require(!guard_runtime_take_event(&event),
                 "Whisper New suppresses transient optical events")) return 1;
    /* Simulate completion/resume, then exercise the independent repeat-person Whisper. */
    vm.route_id=6u;vm.suspended.valid=false;
    guard_runtime_observe(&vm,5000u,1500u);
    guard_runtime_observe(&vm,5000u,1600u);
    guard_runtime_set_input_locked(true);
    guard_runtime_observe(&vm,8000u,1700u);
    guard_runtime_observe(&vm,8000u,1800u);
    GuardRuntimeEvent locked_event;
    if(!require(guard_runtime_take_event(&locked_event),"locked Whisper event")||
       !require(locked_event.type==GUARD_EVENT_STATE&&vm.route_id==6u,
                "Whisper waits while input is locked"))return 1;
    guard_runtime_set_input_locked(false);
    guard_runtime_service(&vm,1900u);
    if(!require(guard_runtime_take_event(&locked_event),"released Whisper event")||
       !require(locked_event.type==GUARD_EVENT_ROUTE&&
                locked_event.route_id==12u&&vm.route_id==12u,
                "Whisper interrupts only after input release"))return 1;
    if (!require(vm.suspended.valid, "repeat light Whisper interrupts any non-restart route")) return 1;
    guard_runtime_observe(&vm,1000u,2000u);
    guard_runtime_observe(&vm,1000u,2100u);
    if (!require(vm.route_id==12u&&vm.suspended.valid,
                 "Desktop light cannot preempt Whisper Repeat") ||
        !require(!guard_runtime_take_event(&event),
                 "Whisper Repeat suppresses transient optical events")) return 1;
    vm.route_id=8u;vm.suspended.valid=false;
    guard_runtime_observe(&vm,5000u,2200u);
    guard_runtime_observe(&vm,5000u,2300u);
    if (!stable(&vm, 6000u, 2500u, 9u, 5u)) return 1;
    guard_runtime_observe(&vm, 5000u, 2700u);
    guard_runtime_observe(&vm, 5000u, 2800u);
    if (!require(guard_runtime_take_event(&event), "targeted return") ||
        !require(event.type == GUARD_EVENT_STATE && event.route_id == 0u,
                 "Game does not replay after Targeted")) return 1;
    if (!stable(&vm, 2000u, 2900u, 5u, 2u)) return 1;
    if (!require(guard_runtime_active_profile() == 2u, "DC profile") ||
        !require(guard_runtime_stage() == 2u, "DC resets stage")) return 1;
    /*
     * Disconnect is the only optical profile allowed to preempt Whisper.
     * Start directly at Game, enter a non-light Whisper, then hold DC for its
     * configured stability window.
     */
    if (!require(guard_runtime_start(3100u), "restart Guard for DC priority") ||
        !stable(&vm,5000u,3100u,8u,5u) ||
        !require(abvm_interrupt_route(&vm,10u,3250u),
                 "Whisper before DC")) return 1;
    guard_runtime_observe(&vm,2000u,3300u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_STATE &&
                 vm.route_id==10u,
                 "DC candidate respects stability during Whisper")) return 1;
    guard_runtime_observe(&vm,2000u,3400u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_ROUTE &&
                 event.route_id==5u && vm.route_id==5u &&
                 guard_runtime_stage()==2u,
                 "stable DC preempts Whisper and starts recovery")) return 1;
    guard_runtime_stop();
    if (!require(!guard_runtime_running(), "stop")) return 1;
    free(image);
    puts("ABVM native global Guard state machine smoke passed");
    return 0;
}
