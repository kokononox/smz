#ifndef AMS_ABVM_CALIBRATION_STORE_H
#define AMS_ABVM_CALIBRATION_STORE_H
#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"
void calibration_store_init(const AbvmVm *vm);
bool calibration_store_light_get(uint8_t profile_id, uint32_t *low_tenths, uint32_t *high_tenths);
bool calibration_store_light_set(uint8_t profile_id, uint32_t low_tenths, uint32_t high_tenths);
bool calibration_store_light_update(uint16_t update_mask, const uint32_t low_tenths[9],
                                    const uint32_t high_tenths[9]);
bool calibration_store_sound_get(uint16_t profile_id, uint16_t *threshold, uint16_t *minimum_ms);
bool calibration_store_sound_set(uint16_t profile_id, uint16_t threshold, uint16_t minimum_ms,
                                 uint16_t silence_max, uint16_t sound_peak);
bool calibration_store_cycle_armed(void);
uint8_t calibration_store_cycle_count(void);
bool calibration_store_cycle_arm_next(uint8_t maximum);
bool calibration_store_cycle_clear_armed(void);
bool calibration_store_cycle_reset(void);
uint8_t calibration_store_shift_attempts(void);
uint8_t calibration_store_shift_target(void);
bool calibration_store_shift_begin(uint8_t target,uint8_t maximum);
bool calibration_store_shift_complete(void);
bool calibration_store_shift_clear(void);
/* Shift-wake state.  The Pico owns no battery-backed clock, so the monotonic
 * wake deadline itself cannot be persisted: what is persisted is the decision
 * the board needs after a reset (was the host asleep, was a wake still owed, how
 * many bounded recovery pulses were spent) plus the window start it was aiming
 * at, which keeps the boot log readable. */
typedef struct WakeStoreState {
    bool host_asleep;
    bool pending;
    uint8_t recovery_attempts;
    uint16_t next_start;
} WakeStoreState;
bool calibration_store_wake_get(WakeStoreState *state);
bool calibration_store_wake_set(const WakeStoreState *state);
bool calibration_store_wake_recovery_reset(void);
uint32_t calibration_store_revision(void);
#endif
