#ifndef AMS_ABVM_LIGHT_SENSOR_H
#define AMS_ABVM_LIGHT_SENSOR_H

#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"

typedef enum LightWatchSubmit {
    LIGHT_WATCH_UNSUPPORTED = 0,
    LIGHT_WATCH_ACCEPTED = 1,
    LIGHT_WATCH_BUSY = 2,
    LIGHT_WATCH_INVALID = 3,
    LIGHT_WATCH_NO_SENSOR = 4,
} LightWatchSubmit;

typedef struct LightCalibrationResult {
    bool valid;
    bool dual_sensor;
    uint32_t minimum_lux;
    uint32_t maximum_lux;
    uint32_t average_lux;
    uint32_t average_tenths;
    uint32_t average_primary_tenths;
    uint32_t average_secondary_tenths;
    /* The Guard stores M=(L1+L2)/2 as twice-M in tenths, preserving half-tenths. */
    uint32_t mean_center_half_tenths;
    int32_t difference_center_tenths;
    uint32_t samples;
} LightCalibrationResult;

typedef struct LightSensorSnapshot {
    bool primary_valid;
    bool secondary_present;
    bool secondary_valid;
    uint32_t primary_lux_tenths;
    uint32_t primary_age_ms;
    uint32_t secondary_lux_tenths;
    uint32_t secondary_age_ms;
} LightSensorSnapshot;

void light_sensor_init(uint32_t now);
void light_sensor_service(AbvmVm *vm, uint32_t now);
LightWatchSubmit light_sensor_arm(const AbvmVm *vm, const AbvmEvent *event,
                                  uint32_t now);
LightWatchSubmit light_sensor_live_start(uint32_t low_lux, uint32_t high_lux,
                                         uint32_t stable_ms, uint32_t timeout_ms,
                                         uint8_t mode, uint32_t now);
bool light_sensor_live_take(bool *detected, uint32_t *lux_tenths);
void light_sensor_cancel_watch(uint32_t now);
bool light_sensor_present(void);
bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now);
bool light_sensor_latest_pair(LightSensorSnapshot *snapshot, uint32_t now);
bool light_sensor_calibration_start(uint32_t duration_ms, uint32_t now);
bool light_sensor_pair_calibration_start(uint32_t duration_ms, uint32_t now);
bool light_sensor_calibration_take(LightCalibrationResult *result);
bool light_sensor_watch_active(void);
bool light_sensor_calibration_active(void);
bool light_sensor_take_fault(void);

#endif
