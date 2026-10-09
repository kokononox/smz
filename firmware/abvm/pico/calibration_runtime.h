#ifndef AMS_ABVM_CALIBRATION_RUNTIME_H
#define AMS_ABVM_CALIBRATION_RUNTIME_H
#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"
typedef enum CalibrationMode { CAL_MODE_NONE=0,CAL_MODE_LIGHT=1,CAL_MODE_SOUND=2 } CalibrationMode;
void calibration_runtime_init(const AbvmVm *vm);
bool calibration_runtime_active(void);
CalibrationMode calibration_runtime_mode(void);
bool calibration_runtime_blue_short(uint32_t now);
bool calibration_runtime_blue_long(uint32_t now);
bool calibration_runtime_yellow_short(uint32_t now);
bool calibration_runtime_yellow_long(uint32_t now);
void calibration_runtime_service(uint32_t now);
bool calibration_runtime_take_event(char *out, uint32_t size);
#endif
