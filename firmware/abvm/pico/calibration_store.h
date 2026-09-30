#ifndef AMS_ABVM_CALIBRATION_STORE_H
#define AMS_ABVM_CALIBRATION_STORE_H
#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"
void calibration_store_init(const AbvmVm *vm);
bool calibration_store_light_get(uint8_t profile_id, uint32_t *low_tenths, uint32_t *high_tenths);
bool calibration_store_light_set(uint8_t profile_id, uint32_t low_tenths, uint32_t high_tenths);
bool calibration_store_light_update(uint8_t update_mask, const uint32_t low_tenths[6],
                                    const uint32_t high_tenths[6]);
bool calibration_store_sound_get(uint16_t profile_id, uint16_t *threshold, uint16_t *minimum_ms);
bool calibration_store_sound_set(uint16_t profile_id, uint16_t threshold, uint16_t minimum_ms,
                                 uint16_t silence_max, uint16_t sound_peak);
bool calibration_store_cycle_armed(void);
uint8_t calibration_store_cycle_count(void);
bool calibration_store_cycle_arm_next(uint8_t maximum);
bool calibration_store_cycle_clear_armed(void);
bool calibration_store_cycle_reset(void);
uint32_t calibration_store_revision(void);
#endif
