#ifndef AMS_ABVM_CYCLE_RUNTIME_H
#define AMS_ABVM_CYCLE_RUNTIME_H

#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"
#include "arm_uart_mouse.h"

typedef enum CycleAction {
    CYCLE_ACTION_NONE = 0,
    CYCLE_ACTION_EXPIRE = 1,
    CYCLE_ACTION_START_STARTUP = 2,
    CYCLE_ACTION_START_FINISH = 3,
    CYCLE_ACTION_SHIFT_STALLED = 4,
} CycleAction;

typedef enum CycleEventType {
    CYCLE_EVENT_NONE = 0,
    CYCLE_EVENT_ARMED = 1,
    CYCLE_EVENT_RESUMED = 2,
    CYCLE_EVENT_DEADLINE = 3,
    CYCLE_EVENT_AFTER_START = 4,
    CYCLE_EVENT_AFTER_COMPLETE = 5,
    CYCLE_EVENT_USB = 6,
    CYCLE_EVENT_STARTUP_START = 7,
    CYCLE_EVENT_STARTUP_COMPLETE = 8,
    CYCLE_EVENT_CANCELLED = 9,
    CYCLE_EVENT_BLOCKED = 10,
    CYCLE_EVENT_FAILED = 11,
    CYCLE_EVENT_ARMED_AT_BOOT = 12,
    CYCLE_EVENT_FINISH_START = 13,
    CYCLE_EVENT_FINISH_COMPLETE = 14,
} CycleEventType;

typedef struct CycleEvent {
    uint8_t type;
    uint8_t count;
    uint8_t down_seen;
    uint8_t host_state;
    uint8_t startup_gate;
    uint16_t route_id;
    uint32_t seconds;
    uint32_t range_min_seconds;
    uint32_t range_max_seconds;
} CycleEvent;

bool cycle_runtime_init(const AbvmVm *vm, uint32_t now);
bool cycle_runtime_available(void);
/* A session found in flash at this board's own boot.  It is held outside every
 * phase until the board can answer whether the shift it belongs to is still open:
 * the wall clock it needs is gone with the power, and nothing may be blocked while
 * it waits for one. */
bool cycle_runtime_session_pending(void);
bool cycle_runtime_session_live(void);
bool cycle_runtime_session_adopt(uint32_t now);
void cycle_runtime_session_drop(void);
/* A session that came back from a power cut never finished its round, so the
 * settle the Startup route opens with -- the rest that follows a round which did
 * finish -- is not owed to it.  Only that one route's opening delay is left out. */
bool cycle_runtime_skip_startup_settle(void);
bool cycle_runtime_waiting_for_usb(void);
bool cycle_runtime_restart_critical(void);
void cycle_runtime_hold(uint32_t now);
void cycle_runtime_continue(uint32_t now);
bool cycle_runtime_held(void);
bool cycle_runtime_manual_start(uint32_t now);
void cycle_runtime_manual_stop(void);
CycleAction cycle_runtime_service(uint32_t now, bool host_seen,
                                  ArmHostUsbState host_state,
                                  bool desktop_ready);
bool cycle_runtime_begin_after(uint32_t now);
bool cycle_runtime_begin_shift(uint16_t route,uint8_t target,uint8_t maximum,uint32_t now);
bool cycle_runtime_shift_confirmed(uint32_t now);
void cycle_runtime_begin_startup(void);
void cycle_runtime_begin_finish(void);
bool cycle_runtime_route_complete(uint16_t route_id, uint32_t now);
void cycle_runtime_fail(uint8_t stage);
bool cycle_runtime_take_event(CycleEvent *event);
uint16_t cycle_runtime_after_route(void);
uint16_t cycle_runtime_startup_route(void);
uint16_t cycle_runtime_finish_route(void);
uint8_t cycle_runtime_count(void);

#endif