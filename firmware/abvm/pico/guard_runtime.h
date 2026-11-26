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
    bool dual_sensor;
    uint32_t lux1_tenths;
    uint32_t lux2_tenths;
    int32_t difference_tenths;
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
void guard_runtime_observe_pair(AbvmVm *vm, uint32_t lux1_tenths,
                                uint32_t lux2_tenths, uint32_t now);
bool guard_runtime_route_complete(AbvmVm *vm, uint16_t route_id, uint32_t now);
bool guard_runtime_take_event(GuardRuntimeEvent *event);
bool guard_runtime_dual_sensor_enabled(void);
bool guard_runtime_dual_sensor_ready(uint32_t now);
uint16_t guard_runtime_dual_calibration_mask(void);
bool guard_runtime_profile_pair_calibrated(uint8_t profile_id);
bool guard_runtime_get_profile_pair_calibration(uint8_t profile_id,
    uint32_t *mean_center_half_tenths, uint32_t *mean_tolerance_half_tenths,
    int32_t *difference_center_tenths, uint32_t *difference_tolerance_tenths);
bool guard_runtime_validate_profile_pair_centers_mask(
    uint16_t calibrated_mask,
    const uint32_t mean_centers_half_tenths[9],
    const int32_t difference_centers_tenths[9]);
bool guard_runtime_validate_profile_pair_centers(
    const uint32_t mean_centers_half_tenths[9],
    const int32_t difference_centers_tenths[9]);
bool guard_runtime_validate_profile_pair_calibrations_mask(
    uint16_t calibrated_mask,
    const uint32_t mean_centers_half_tenths[9],
    const uint32_t mean_tolerances_half_tenths[9],
    const int32_t difference_centers_tenths[9],
    const uint32_t difference_tolerances_tenths[9]);
bool guard_runtime_validate_profile_pair_center(uint8_t profile_id,
    uint32_t mean_center_half_tenths, int32_t difference_center_tenths);
bool guard_runtime_set_profile_pair_centers_mask(uint16_t update_mask,
    const uint32_t mean_centers_half_tenths[9],
    const int32_t difference_centers_tenths[9]);
bool guard_runtime_set_profile_pair_calibrations_mask(uint16_t update_mask,
    const uint32_t mean_centers_half_tenths[9],
    const uint32_t mean_tolerances_half_tenths[9],
    const int32_t difference_centers_tenths[9],
    const uint32_t difference_tolerances_tenths[9]);
bool guard_runtime_set_profile_pair_centers_batch(
    const uint32_t mean_centers_half_tenths[9],
    const int32_t difference_centers_tenths[9]);
bool guard_runtime_set_profile_pair_centers(uint8_t profile_id,
    uint32_t mean_center_half_tenths, int32_t difference_center_tenths);
bool guard_runtime_running(void);
bool guard_runtime_paused(void);
bool guard_runtime_watchdog_tripped(void);
uint8_t guard_runtime_active_profile(void);
uint8_t guard_runtime_stage(void);
uint8_t guard_runtime_expected_profile(void);
uint32_t guard_runtime_stage_elapsed(uint32_t now);
uint32_t guard_runtime_watchdog_timeout_ms(void);
uint8_t guard_runtime_calibration_cue(uint8_t profile_id);
bool guard_runtime_calibration_pattern(uint8_t profile_id, uint16_t hz[8],
    uint16_t duration_ms[8], uint16_t gap_ms[8], uint8_t *count,
    uint8_t *volume, uint8_t *envelope);
bool guard_runtime_calibration_style(uint8_t profile_id, uint8_t *volume,
                                     uint8_t *envelope);
bool guard_runtime_buzzer_cue(uint8_t cue_id, uint16_t hz[8],
    uint16_t duration_ms[8], uint16_t gap_ms[8], uint8_t *count,
    uint8_t *volume, uint8_t *envelope);
const char *guard_runtime_profile_name(uint8_t profile_id);
bool guard_runtime_get_profile_range(uint8_t profile_id, uint32_t *low_tenths,
                                     uint32_t *high_tenths);
bool guard_runtime_profile_matches(uint8_t profile_id, uint32_t lux_tenths);
bool guard_runtime_profile_matches_pair(uint8_t profile_id,
                                        uint32_t lux1_tenths,
                                        uint32_t lux2_tenths);
bool guard_runtime_set_profile_range(uint8_t profile_id, uint32_t low_tenths, uint32_t high_tenths);

#endif
