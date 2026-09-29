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
    uint32_t minimum_lux;
    uint32_t maximum_lux;
    uint32_t average_lux;
    uint32_t samples;
} LightCalibrationResult;

void light_sensor_init(uint32_t now);
void light_sensor_service(AbvmVm *vm, uint32_t now);
LightWatchSubmit light_sensor_arm(const AbvmVm *vm, const AbvmEvent *event,
                                  uint32_t now);
void light_sensor_cancel_watch(uint32_t now);
bool light_sensor_present(void);
bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now);
bool light_sensor_calibration_start(uint32_t duration_ms, uint32_t now);
bool light_sensor_calibration_take(LightCalibrationResult *result);
bool light_sensor_watch_active(void);
bool light_sensor_calibration_active(void);
bool light_sensor_take_fault(void);

#endif
