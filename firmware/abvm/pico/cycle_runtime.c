#include "cycle_runtime.h"

#include <string.h>
#include "calibration_store.h"

#define CYCLE_VERSION 2u
#define CYCLE_DESCRIPTOR_SIZE 20u
#define CYCLE_FLAG_AUTO_RESUME 1u
#define CYCLE_DESKTOP_STABLE_MS 1000u
#define CYCLE_DESKTOP_FALLBACK_MS 30000u

typedef enum CyclePhase {
    CYCLE_IDLE = 0,
    CYCLE_RUN = 1,
    CYCLE_AFTER = 2,
    CYCLE_WAIT_USB = 3,
    CYCLE_STARTUP = 4,
    CYCLE_FINISH = 5,
} CyclePhase;

typedef struct CycleState {
    bool available;
    bool auto_resume;
    bool down_seen;
    bool up_timing;
    bool desktop_timing;
    bool held;
    uint8_t startup_gate;
    uint8_t phase;
    uint8_t max_restarts;
    uint8_t last_host_state;
    uint16_t after_route,switch_route;
    uint16_t startup_route;
    uint16_t finish_route;
    uint32_t run_min_ms;
    uint32_t run_max_ms;
    uint32_t usb_stable_ms;
    uint32_t deadline;
    uint32_t up_since;
    uint32_t desktop_since;
    uint32_t held_remaining_ms;
    uint32_t prng,shift_wait_since;
    CycleEvent pending;
    bool event_pending;
} CycleState;

static CycleState cycle;

static uint16_t read_u16(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}
static uint32_t read_u32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static bool reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}
static uint32_t random_next(void) {
    uint32_t x=cycle.prng?cycle.prng:0x6d2b79f5u;
    x^=x<<13;x^=x>>17;x^=x<<5;cycle.prng=x;return x;
}
static uint32_t choose_duration(void) {
    if(cycle.run_max_ms<=cycle.run_min_ms)return cycle.run_min_ms;
    return cycle.run_min_ms+
        random_next()%(cycle.run_max_ms-cycle.run_min_ms+1u);
}
static void emit(uint8_t type) {
    memset(&cycle.pending,0,sizeof(cycle.pending));
    cycle.pending.type=type;
    cycle.pending.count=calibration_store_cycle_count();
    cycle.event_pending=true;
}
static void arm_run(uint32_t now,bool resumed) {
    uint32_t duration=choose_duration();
    cycle.phase=CYCLE_RUN;
    cycle.deadline=now+duration;
    cycle.down_seen=false;
    cycle.up_timing=false;
    cycle.desktop_timing=false;
    cycle.startup_gate=0u;
    cycle.held=false;
    cycle.held_remaining_ms=0u;
    emit(resumed?CYCLE_EVENT_RESUMED:CYCLE_EVENT_ARMED);
    cycle.pending.seconds=duration/1000u;
    cycle.pending.range_min_seconds=cycle.run_min_ms/1000u;
    cycle.pending.range_max_seconds=cycle.run_max_ms/1000u;
}

bool cycle_runtime_init(const AbvmVm *vm,uint32_t now) {
    memset(&cycle,0,sizeof(cycle));
    uint16_t constant_id;const uint8_t *raw;uint32_t size;
    if(!vm||!abvm_find_constant(vm,ABVM_CONST_CYCLE,&constant_id,&raw,&size)||
       size!=CYCLE_DESCRIPTOR_SIZE||
       (raw[0]!=1u&&raw[0]!=CYCLE_VERSION)||
       (raw[1]&~CYCLE_FLAG_AUTO_RESUME)||!raw[2]||
       (raw[0]==1u&&raw[3]))
        return false;
    (void)constant_id;
    cycle.auto_resume=(raw[1]&CYCLE_FLAG_AUTO_RESUME)!=0u;
    cycle.max_restarts=raw[2];
    cycle.finish_route=raw[0]>=2u?raw[3]:0u;
    cycle.run_min_ms=read_u32(raw+4u);
    cycle.run_max_ms=read_u32(raw+8u);
    cycle.after_route=read_u16(raw+12u);
    cycle.startup_route=read_u16(raw+14u);
    cycle.usb_stable_ms=read_u32(raw+16u);
    if(!cycle.run_min_ms||cycle.run_min_ms>cycle.run_max_ms||
       !cycle.after_route||!cycle.startup_route||
       (raw[0]>=2u&&!cycle.finish_route)||
       cycle.usb_stable_ms<250u)return false;
    cycle.available=true;
    cycle.prng=now^read_u32(vm->header.program_sha256);
    if(cycle.auto_resume&&(calibration_store_cycle_armed()||calibration_store_shift_target())){
        cycle.phase=CYCLE_WAIT_USB;cycle.shift_wait_since=now;
        cycle.down_seen=true; /* an armed marker present at Pico boot proves a reboot */
        emit(CYCLE_EVENT_ARMED_AT_BOOT);
    }
    return true;
}
bool cycle_runtime_available(void){return cycle.available;}
bool cycle_runtime_waiting_for_usb(void){
    return cycle.phase==CYCLE_WAIT_USB||cycle.phase==CYCLE_AFTER;
}
bool cycle_runtime_restart_critical(void){
    return cycle.phase==CYCLE_AFTER||cycle.phase==CYCLE_WAIT_USB||
           cycle.phase==CYCLE_STARTUP||cycle.phase==CYCLE_FINISH;
}
void cycle_runtime_hold(uint32_t now) {
    if(!cycle.available||cycle.held)return;
    cycle.held=true;
    cycle.held_remaining_ms=cycle.phase==CYCLE_RUN&&!reached(now,cycle.deadline)?
        cycle.deadline-now:0u;
}
void cycle_runtime_continue(uint32_t now) {
    if(!cycle.available||!cycle.held)return;
    if(cycle.phase==CYCLE_RUN)
        cycle.deadline=now+cycle.held_remaining_ms;
    cycle.held=false;
    cycle.held_remaining_ms=0u;
}
bool cycle_runtime_held(void){return cycle.held;}
bool cycle_runtime_manual_start(uint32_t now) {
    if(!cycle.available)return false;
    /* A stale terminal count must never select Finish in a new operator
     * session.  Do not arm the run unless the persistent reset succeeded. */
    if(!calibration_store_cycle_reset()){
        cycle.phase=CYCLE_IDLE;cycle.deadline=0u;
        emit(CYCLE_EVENT_FAILED);return false;
    }
    arm_run(now,false);
    return true;
}
void cycle_runtime_manual_stop(void) {
    if(!cycle.available)return;
    (void)calibration_store_cycle_reset();
    cycle.phase=CYCLE_IDLE;cycle.deadline=0u;cycle.up_timing=false;
    cycle.held=false;cycle.held_remaining_ms=0u;
    emit(CYCLE_EVENT_CANCELLED);
}
CycleAction cycle_runtime_service(uint32_t now,bool host_seen,
                                  ArmHostUsbState host_state,
                                  bool desktop_ready) {
    if(!cycle.available)return CYCLE_ACTION_NONE;
    if(cycle.held)return CYCLE_ACTION_NONE;
    if(cycle.phase==CYCLE_RUN&&reached(now,cycle.deadline)){
        cycle.deadline=0u;
        uint8_t count=calibration_store_cycle_count();
        if(count>cycle.max_restarts){
            cycle_runtime_fail(5u);
            return CYCLE_ACTION_NONE;
        }
        if(cycle.finish_route&&count==cycle.max_restarts)
            return CYCLE_ACTION_START_FINISH;
        emit(CYCLE_EVENT_DEADLINE);
        return CYCLE_ACTION_EXPIRE;
    }
    if(cycle.phase!=CYCLE_AFTER&&cycle.phase!=CYCLE_WAIT_USB)
        return CYCLE_ACTION_NONE;
    if(cycle.phase==CYCLE_WAIT_USB&&calibration_store_shift_target()&&
       reached(now,cycle.shift_wait_since+180000u))return CYCLE_ACTION_SHIFT_STALLED;
    if(host_seen&&host_state!=ARM_HOST_USB_UNKNOWN){
        if(cycle.last_host_state!=(uint8_t)host_state){
            cycle.last_host_state=(uint8_t)host_state;
            emit(CYCLE_EVENT_USB);
            cycle.pending.host_state=(uint8_t)host_state;
        }
        if(host_state==ARM_HOST_USB_DOWN||host_state==ARM_HOST_USB_SUSPEND){
            cycle.down_seen=true;cycle.up_timing=false;
            cycle.desktop_timing=false;
        /* A freshly armed warm corrective switch is NOT evidence of a reboot.
         * Only observed USB down/suspend (or the marker restored at Pico boot,
         * where switch_route is zero) authorizes Startup. */
        }else if(host_state==ARM_HOST_USB_UP&&
                 (cycle.down_seen||(!cycle.switch_route&&calibration_store_cycle_armed()))){
            if(!cycle.up_timing){cycle.up_timing=true;cycle.up_since=now;}
            if(!desktop_ready)cycle.desktop_timing=false;
            else if(!cycle.desktop_timing){
                cycle.desktop_timing=true;cycle.desktop_since=now;
            }
            bool usb_stable=reached(now,cycle.up_since+cycle.usb_stable_ms);
            bool desktop_stable=cycle.desktop_timing&&
                reached(now,cycle.desktop_since+CYCLE_DESKTOP_STABLE_MS);
            /* Never strand a later round on a marginal light calibration. */
            bool desktop_fallback=usb_stable&&reached(now,
                cycle.up_since+cycle.usb_stable_ms+
                CYCLE_DESKTOP_FALLBACK_MS);
            if(cycle.phase==CYCLE_WAIT_USB&&usb_stable&&
               (desktop_stable||desktop_fallback)){
                cycle.startup_gate=desktop_stable?1u:2u;
                cycle.up_timing=false;
                cycle.desktop_timing=false;
                return CYCLE_ACTION_START_STARTUP;
            }
        }
    }
    return CYCLE_ACTION_NONE;
}
bool cycle_runtime_begin_shift(uint16_t route,uint8_t target,uint8_t maximum,uint32_t now) {
    (void)now;
    if(!cycle.available||!cycle.auto_resume||(route!=16u&&route!=17u)||
       !calibration_store_shift_begin(target,maximum))return false;
    cycle.switch_route=route;cycle.phase=CYCLE_AFTER;cycle.down_seen=false;
    cycle.up_timing=false;cycle.desktop_timing=false;cycle.held=false;
    cycle.last_host_state=ARM_HOST_USB_UNKNOWN;return true;
}
bool cycle_runtime_shift_confirmed(uint32_t now) {
    if(!calibration_store_shift_target())return true;
    if(!calibration_store_shift_complete())return false;
    cycle.switch_route=0u;
    if(cycle.phase==CYCLE_RUN)arm_run(now,false);
    return true;
}
bool cycle_runtime_begin_after(uint32_t now) {
    (void)now;
    if(!cycle.available)return false;
    if(cycle.auto_resume&&
       !calibration_store_cycle_arm_next(cycle.max_restarts)){
        cycle.phase=CYCLE_IDLE;emit(CYCLE_EVENT_BLOCKED);return false;
    }
    cycle.phase=CYCLE_AFTER;cycle.down_seen=false;cycle.up_timing=false;
    cycle.desktop_timing=false;
    cycle.last_host_state=ARM_HOST_USB_UNKNOWN;
    emit(CYCLE_EVENT_AFTER_START);cycle.pending.route_id=cycle.after_route;
    return true;
}
void cycle_runtime_begin_startup(void) {
    cycle.phase=CYCLE_STARTUP;
    emit(CYCLE_EVENT_STARTUP_START);cycle.pending.route_id=cycle.startup_route;
    cycle.pending.startup_gate=cycle.startup_gate;
}
void cycle_runtime_begin_finish(void) {
    cycle.phase=CYCLE_FINISH;
    emit(CYCLE_EVENT_FINISH_START);cycle.pending.route_id=cycle.finish_route;
}
bool cycle_runtime_route_complete(uint16_t route_id,uint32_t now) {
    if(cycle.phase==CYCLE_AFTER&&route_id==(cycle.switch_route?cycle.switch_route:cycle.after_route)){
        if(cycle.auto_resume){
            cycle.phase=CYCLE_WAIT_USB;cycle.shift_wait_since=now;
            emit(CYCLE_EVENT_AFTER_COMPLETE);
            cycle.pending.down_seen=cycle.down_seen?1u:0u;
        }else cycle.phase=CYCLE_IDLE;
        return false;
    }
    if(cycle.phase==CYCLE_STARTUP&&route_id==cycle.startup_route){
        if(!calibration_store_cycle_clear_armed()){
            cycle.phase=CYCLE_WAIT_USB;emit(CYCLE_EVENT_FAILED);return false;
        }
        emit(CYCLE_EVENT_STARTUP_COMPLETE);
        arm_run(now,true);
        return true;
    }
    if(cycle.phase==CYCLE_FINISH&&route_id==cycle.finish_route){
        cycle.phase=CYCLE_IDLE;cycle.deadline=0u;cycle.held=false;
        emit(CYCLE_EVENT_FINISH_COMPLETE);
        /* Finish is terminal for this session, not a permanent marker.  Clear
         * the count so a later power-on cannot inherit the five-round limit. */
        (void)calibration_store_cycle_reset();
        return false;
    }
    return false;
}
void cycle_runtime_fail(uint8_t stage) {
    if(!cycle.available)return;
    (void)stage;
    (void)calibration_store_cycle_reset();
    cycle.phase=CYCLE_IDLE;cycle.deadline=0u;cycle.up_timing=false;
    emit(CYCLE_EVENT_FAILED);
}
bool cycle_runtime_take_event(CycleEvent *event) {
    if(!cycle.event_pending||!event)return false;
    *event=cycle.pending;cycle.event_pending=false;return true;
}
uint16_t cycle_runtime_after_route(void){return cycle.after_route;}
uint16_t cycle_runtime_startup_route(void){return cycle.startup_route;}
uint16_t cycle_runtime_finish_route(void){return cycle.finish_route;}
uint8_t cycle_runtime_count(void){return calibration_store_cycle_count();}