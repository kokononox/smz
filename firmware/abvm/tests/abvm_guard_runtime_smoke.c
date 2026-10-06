#include "abvm_vm.h"
#include "guard_runtime.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool latest_ready;
static uint32_t latest_lux;
bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now) {
    (void)now;
    if(!latest_ready)return false;
    latest_ready=false;*lux_tenths=latest_lux;*age_ms=0u;return true;
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
        !require(guard_runtime_calibration_cue(1u)==8u &&
                 guard_runtime_calibration_cue(8u)==1u &&
                 guard_runtime_calibration_cue(9u)==9u,
                 "custom calibration cue mapping") ||
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
    if (!require(vm.suspended.valid && vm.suspended.route_id==8u,
                 "Targeted suspends the exact Game cursor for resume")) return 1;
    guard_runtime_observe(&vm,5000u,2620u);
    guard_runtime_observe(&vm,1000u,2630u);
    guard_runtime_observe(&vm,9000u,2640u);
    guard_runtime_observe(&vm,7000u,2650u);
    guard_runtime_observe(&vm,123456u,2660u);
    if (!require(vm.route_id==9u&&vm.suspended.valid,
                 "Targeted New ignores every non-DC light until END") ||
        !require(!guard_runtime_take_event(&event),
                 "Targeted New emits no transient optical event")) return 1;
    /* Simulate Targeted END restoring the suspended Game context. */
    vm.route_id=vm.suspended.route_id;
    vm.suspended.valid=false;
    guard_runtime_observe(&vm, 5000u, 2700u);
    guard_runtime_observe(&vm, 5000u, 2800u);
    if (!require(guard_runtime_take_event(&event), "targeted return") ||
        !require(event.type == GUARD_EVENT_STATE && event.route_id == 0u,
                 "Game does not replay after Targeted")) return 1;
    /* Targeted New mirrors Whisper New and therefore has no cooldown. A new
     * optical event can immediately interrupt Game again. */
    if (!stable(&vm,6000u,2820u,9u,5u))
        return 1;
    vm.route_id=8u;vm.suspended.valid=false;
    guard_runtime_observe(&vm,5000u,2940u);
    guard_runtime_observe(&vm,5000u,3050u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_STATE && vm.route_id==8u,
                 "Targeted New returns without arming a cooldown")) return 1;
    if (!stable(&vm, 2000u, 3100u, 5u, 2u)) return 1;
    if (!require(guard_runtime_active_profile() == 2u, "DC profile") ||
        !require(guard_runtime_stage() == 2u, "DC resets stage")) return 1;
    /* Dashboard -> DC must run the dedicated DC macro completely, then
     * chain Login/DC before ordered Dashboard -> Loading -> Game recovery. */
    if (!require(guard_runtime_start(3000u),
                 "restart Guard for Dashboard DC chain") ||
        !stable(&vm,1000u,3000u,1u,1u) ||
        !stable(&vm,2000u,3200u,4u,2u) ||
        !stable(&vm,3000u,3400u,6u,3u) ||
        !stable(&vm,2000u,3600u,5u,2u)) return 1;
    guard_runtime_observe(&vm,3000u,3800u);
    guard_runtime_observe(&vm,3000u,3900u);
    if (!require(vm.route_id==5u && !guard_runtime_take_event(&event),
                 "Dashboard light cannot abort the DC macro")) return 1;
    if (!require(guard_runtime_route_complete(&vm,5u,4000u),
                 "DC completion is handled") ||
        !require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_ROUTE && event.route_id==4u &&
                 event.stage==2u && event.reason &&
                 !strcmp(event.reason,"dc-complete-to-login-or-dc") &&
                 vm.route_id==4u,
                 "DC chains Login/DC")) return 1;
    if (!stable(&vm,3000u,4200u,6u,3u) ||
        !stable(&vm,4000u,4400u,7u,4u) ||
        !stable(&vm,5000u,4600u,8u,5u)) return 1;

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
                 guard_runtime_active_profile()==2u &&
                 guard_runtime_stage()==2u,
                 "stable DC preempts Whisper and starts recovery")) return 1;
    /*
     * Targeted has the same optical lock: all ordinary scene changes are
     * ignored, but a stable disconnect may replace it with recovery.
     */
    if (!require(guard_runtime_start(3500u),
                 "restart Guard for Targeted DC priority") ||
        !stable(&vm,5000u,3500u,8u,5u) ||
        !stable(&vm,6000u,3700u,9u,5u)) return 1;
    guard_runtime_observe(&vm,1000u,3850u);
    guard_runtime_observe(&vm,7000u,3860u);
    if (!require(vm.route_id==9u&&vm.suspended.valid,
                 "non-DC light cannot preempt Targeted") ||
        !require(!guard_runtime_take_event(&event),
                 "Targeted suppresses non-DC optical events")) return 1;
    guard_runtime_observe(&vm,2000u,3900u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_STATE && vm.route_id==9u,
                 "DC candidate respects stability during Targeted")) return 1;
    guard_runtime_observe(&vm,2000u,4000u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_ROUTE &&
                 event.route_id==5u && vm.route_id==5u &&
                 guard_runtime_active_profile()==2u &&
                 guard_runtime_stage()==2u,
                 "stable DC preempts Targeted and starts recovery")) return 1;
    /*
     * A completed optical Whisper cannot be re-armed by a quick light bounce.
     * The per-profile board-only cooldown remains effective with sound off.
     */
    if (!require(guard_runtime_start(4000u), "restart Guard for cooldown") ||
        !stable(&vm,5000u,4000u,8u,5u) ||
        !stable(&vm,8000u,4200u,12u,5u)) return 1;
    vm.route_id=8u;vm.suspended.valid=false;
    guard_runtime_observe(&vm,5000u,4400u);
    guard_runtime_observe(&vm,5000u,4500u);
    (void)guard_runtime_take_event(&event);
    guard_runtime_observe(&vm,8000u,4600u);
    guard_runtime_observe(&vm,8000u,4700u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_DENIED &&
                 event.reason &&
                 !strcmp(event.reason,"light-whisper-cooldown") &&
                 vm.route_id==8u,
                 "light Whisper cooldown suppresses quick retrigger")) return 1;
    /*
     * Targeted New and Targeted Repeat mirror the two optical Whisper
     * classifiers: separate light ranges, separate routes, and independent
     * board-local cooldown clocks.
     */
    if (!require(guard_runtime_start(5000u),
                 "restart Guard for split Targeted") ||
        !stable(&vm,5000u,5000u,8u,5u) ||
        !stable(&vm,9000u,5200u,14u,5u)) return 1;
    if (!require(vm.suspended.valid && vm.suspended.route_id==8u,
                 "Targeted Repeat suspends the Game cursor")) return 1;
    guard_runtime_observe(&vm,5000u,5310u);
    guard_runtime_observe(&vm,1000u,5320u);
    guard_runtime_observe(&vm,6000u,5330u);
    guard_runtime_observe(&vm,8000u,5340u);
    if (!require(vm.route_id==14u&&vm.suspended.valid,
                 "Targeted Repeat ignores every non-DC light until END") ||
        !require(!guard_runtime_take_event(&event),
                 "Targeted Repeat emits no transient optical event")) return 1;
    vm.route_id=8u;vm.suspended.valid=false;
    guard_runtime_observe(&vm,5000u,5400u);
    guard_runtime_observe(&vm,5000u,5500u);
    (void)guard_runtime_take_event(&event);
    guard_runtime_observe(&vm,9000u,5600u);
    guard_runtime_observe(&vm,9000u,5700u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_DENIED && event.reason &&
                 !strcmp(event.reason,"light-targeted-cooldown") &&
                 vm.route_id==8u,
                 "Targeted Repeat owns an independent cooldown")) return 1;
    guard_runtime_observe(&vm,5000u,7800u);
    guard_runtime_observe(&vm,5000u,7900u);
    (void)guard_runtime_take_event(&event);
    if (!stable(&vm,6000u,8000u,9u,5u) ||
        !require(vm.suspended.valid && vm.suspended.route_id==8u,
                 "Targeted New cooldown does not block Targeted Repeat policy"))
        return 1;
    /*
     * Post-restart Guard skips Desktop, then requires the complete ordered
     * Login -> Dashboard -> Loading -> Game sequence. Each accepted stage
     * re-arms the operator watchdog; Game disarms it.
     */
    if (!require(guard_runtime_start_after_restart(10000u),
                 "post-restart Guard start") ||
        !require(guard_runtime_stage()==1u &&
                 guard_runtime_expected_profile()==2u &&
                 guard_runtime_watchdog_timeout_ms()==60000u,
                 "post-restart expects Login/DC")) return 1;
    if (!stable(&vm,2000u,11000u,4u,2u) ||
        !require(guard_runtime_expected_profile()==3u,"expect Dashboard") ||
        !stable(&vm,3000u,11200u,6u,3u) ||
        !require(guard_runtime_expected_profile()==4u,"expect Loading") ||
        !stable(&vm,4000u,11400u,7u,4u) ||
        !require(guard_runtime_expected_profile()==5u,"expect Game") ||
        !stable(&vm,5000u,11600u,8u,5u) ||
        !require(guard_runtime_expected_profile()==0u,
                 "Game disarms stage watchdog")) return 1;
    if (!require(guard_runtime_start_after_restart(20000u),
                 "post-restart Watchdog start")) return 1;
    guard_runtime_service(&vm,80000u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_WATCHDOG_TRIPPED &&
                 guard_runtime_paused() &&
                 guard_runtime_watchdog_tripped() &&
                 guard_runtime_expected_profile()==2u,
                 "missing Login pauses Guard for operator")) return 1;
    if (!require(guard_runtime_resume() &&
                 !guard_runtime_paused() &&
                 !guard_runtime_watchdog_tripped(),
                 "manual Resume acknowledges Watchdog without skipping stage"))
        return 1;
    /* If Loading was not calibrated or was too brief to become stable, Game
     * is first rejected while stage 3 still expects profile 4.  A Watchdog
     * acknowledgement is an explicit operator confirmation, so the same
     * already-latched Game scene must be sampled again and may catch up. */
    if (!require(guard_runtime_start_after_restart(81000u),
                 "restart Guard for manual Game catch-up") ||
        !stable(&vm,2000u,82000u,4u,2u) ||
        !stable(&vm,3000u,82200u,6u,3u)) return 1;
    guard_runtime_observe(&vm,5000u,82400u);
    guard_runtime_observe(&vm,5000u,82500u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_DENIED &&
                 event.reason && !strcmp(event.reason,"game-not-expected"),
                 "Game is ordered before operator acknowledgement")) return 1;
    guard_runtime_service(&vm,142300u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_WATCHDOG_TRIPPED &&
                 guard_runtime_expected_profile()==4u,
                 "missing Loading trips stage Watchdog")) return 1;
    if (!require(guard_runtime_resume(),
                 "operator acknowledges missing Loading")) return 1;
    guard_runtime_observe(&vm,5000u,142400u);
    guard_runtime_observe(&vm,5000u,142500u);
    if (!require(guard_runtime_take_event(&event) &&
                 event.type==GUARD_EVENT_ROUTE && event.route_id==8u &&
                 event.stage==5u && event.reason &&
                 !strcmp(event.reason,"manual-watchdog-game-catchup") &&
                 guard_runtime_expected_profile()==0u,
                 "manual Resume catches stable Game up from stage 3")) return 1;
    /*
     * A Whisper that resumes the fishing Game may reveal Targeted light
     * instead of Game light. Keep the exact Game cursor running for one
     * minute; returning to Game cancels the grace without route 9.
     */
    if(!require(guard_runtime_start(100000u),"post-Whisper grace Guard")||
       !stable(&vm,5000u,100000u,8u,5u)||
       !require(abvm_interrupt_route(&vm,10u,100200u),"Whisper interrupt"))
        return 1;
    guard_runtime_service(&vm,100250u);
    vm.route_id=8u;vm.status=ABVM_STATUS_RUNNING;vm.suspended.valid=false;
    guard_runtime_service(&vm,100300u);
    guard_runtime_observe(&vm,6000u,100400u);
    guard_runtime_observe(&vm,6000u,100500u);
    if(!require(guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_STATE&&
                event.reason&&!strcmp(event.reason,
                    "post-whisper-targeted-grace-start")&&
                vm.route_id==8u&&!guard_runtime_paused(),
                "Targeted starts one-minute fishing grace"))return 1;
    guard_runtime_observe(&vm,5000u,101000u);
    guard_runtime_observe(&vm,5000u,101100u);
    if(!require(guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_STATE&&vm.route_id==8u&&
                !guard_runtime_paused(),
                "Game return cancels post-Whisper grace"))return 1;

    /* Persistent Targeted reaches exactly 60 seconds and trips the same
     * fail-safe operator Watchdog instead of starting route 9. */
    if(!require(abvm_interrupt_route(&vm,10u,102000u),
                "second Whisper interrupt"))return 1;
    guard_runtime_service(&vm,102050u);
    vm.route_id=8u;vm.status=ABVM_STATUS_RUNNING;vm.suspended.valid=false;
    latest_lux=6000u;latest_ready=true;
    guard_runtime_service(&vm,102100u);
    guard_runtime_observe(&vm,6000u,102200u);
    guard_runtime_observe(&vm,6000u,102300u);
    (void)guard_runtime_take_event(&event);
    guard_runtime_observe(&vm,6000u,162199u);
    if(!require(!guard_runtime_paused()&&vm.route_id==8u,
                "fishing continues before 60-second boundary"))return 1;
    guard_runtime_observe(&vm,6000u,162200u);
    if(!require(guard_runtime_take_event(&event)&&
                event.type==GUARD_EVENT_WATCHDOG_TRIPPED&&
                event.reason&&!strcmp(event.reason,
                    "post-whisper-targeted-timeout")&&
                guard_runtime_paused()&&
                guard_runtime_watchdog_tripped()&&
                guard_runtime_expected_profile()==5u&&
                guard_runtime_watchdog_timeout_ms()==60000u,
                "persistent Targeted trips operator Watchdog"))return 1;
    if(!require(abvm_resume(&vm,162300u)&&guard_runtime_resume()&&
                !guard_runtime_paused()&&
                guard_runtime_expected_profile()==5u,
                "manual Resume keeps waiting for Game"))return 1;
    guard_runtime_stop();
    if (!require(!guard_runtime_running(), "stop")) return 1;
    free(image);
    puts("ABVM native global Guard state machine smoke passed");
    return 0;
}
