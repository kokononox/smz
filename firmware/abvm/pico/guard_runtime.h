#ifndef AMS_ABVM_GUARD_RUNTIME_H
#define AMS_ABVM_GUARD_RUNTIME_H

#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"

typedef enum GuardRuntimeEventType {
    GUARD_EVENT_NONE = 0,
    GUARD_EVENT_STATE = 1,
    GUARD_EVENT_ROUTE = 2,
    GUARD_EVENT_DENIED = 3,
    GUARD_EVENT_FAULT = 4,
    GUARD_EVENT_WATCHDOG_TRIPPED = 5,
} GuardRuntimeEventType;

typedef struct GuardRuntimeEvent {
    uint8_t type;
    uint8_t profile_id;
    uint8_t stage;
    uint8_t context;
    uint16_t route_id;
    uint32_t lux_tenths;
    const char *reason;
} GuardRuntimeEvent;

bool guard_runtime_init(const AbvmVm *vm);
bool guard_runtime_available(void);
bool guard_runtime_start(uint32_t now);
bool guard_runtime_start_after_restart(uint32_t now);
void guard_runtime_stop(void);
bool guard_runtime_pause(void);
bool guard_runtime_resume(void);
void guard_runtime_service(AbvmVm *vm, uint32_t now);
void guard_runtime_set_input_locked(bool locked);
void guard_runtime_observe(AbvmVm *vm, uint32_t lux_tenths, uint32_t now);
bool guard_runtime_take_event(GuardRuntimeEvent *event);
bool guard_runtime_running(void);
bool guard_runtime_paused(void);
bool guard_runtime_watchdog_tripped(void);
uint8_t guard_runtime_active_profile(void);
uint8_t guard_runtime_stage(void);
uint8_t guard_runtime_expected_profile(void);
uint32_t guard_runtime_stage_elapsed(uint32_t now);
uint32_t guard_runtime_watchdog_timeout_ms(void);
uint8_t guard_runtime_calibration_cue(uint8_t profile_id);
const char *guard_runtime_profile_name(uint8_t profile_id);
bool guard_runtime_get_profile_range(uint8_t profile_id, uint32_t *low_tenths,
                                     uint32_t *high_tenths);
bool guard_runtime_set_profile_range(uint8_t profile_id, uint32_t low_tenths, uint32_t high_tenths);

#endif
