
#ifndef AMS_ABVM_ARM_UART_MOUSE_H
#define AMS_ABVM_ARM_UART_MOUSE_H

#include <stdbool.h>
#include <stdint.h>
#include "abvm_vm.h"

typedef enum ArmMouseSubmit {
    ARM_MOUSE_UNSUPPORTED = 0,
    ARM_MOUSE_ACCEPTED = 1,
    ARM_MOUSE_BUSY = 2,
    ARM_MOUSE_INVALID = 3,
} ArmMouseSubmit;
typedef enum ArmSoundSubmit {
    ARM_SOUND_UNSUPPORTED = 0,
    ARM_SOUND_ACCEPTED = 1,
    ARM_SOUND_BUSY = 2,
    ARM_SOUND_INVALID = 3,
} ArmSoundSubmit;
typedef enum ArmHostUsbState {
    ARM_HOST_USB_UNKNOWN = 0,
    ARM_HOST_USB_DOWN = 1,
    ARM_HOST_USB_SUSPEND = 2,
    ARM_HOST_USB_UP = 3,
} ArmHostUsbState;

void arm_uart_mouse_init(void);
bool arm_uart_mouse_probe(uint32_t now);
ArmMouseSubmit arm_uart_mouse_submit(const AbvmVm *vm, const AbvmEvent *event, uint32_t now);
ArmMouseSubmit arm_uart_mouse_submit_live(const char *command, uint32_t now);
ArmSoundSubmit arm_uart_sound_arm(const AbvmVm *vm, const AbvmEvent *event, uint32_t now);
bool arm_uart_sound_test_start(uint32_t now, uint16_t threshold,
                               uint16_t minimum_ms, uint32_t timeout_ms);
bool arm_uart_sound_restart(uint32_t now, uint16_t profile,
                            uint16_t threshold, uint16_t minimum_ms,
                            uint32_t timeout_ms);
bool arm_uart_mouse_service(uint32_t now, uint8_t *completed_lane);
bool arm_uart_sound_take(uint16_t *profile, bool *detected, uint16_t *peak);
uint16_t arm_uart_sound_threshold(void);
uint16_t arm_uart_sound_minimum(void);
bool arm_uart_sound_uses_calibration(void);
bool arm_uart_sound_calibration_start(uint32_t now, uint16_t duration_ms);
bool arm_uart_sound_calibration_take(uint16_t *average, uint16_t *peak);
void arm_uart_mouse_release_all(uint32_t now);
void arm_uart_mouse_discard_completion(void);
bool arm_uart_mouse_busy(void);
bool arm_uart_mouse_releasing(void);
bool arm_uart_sound_active(void);
bool arm_uart_mouse_faulted(void);
const char *arm_uart_mouse_fault(void);
bool arm_uart_mouse_ready(void);
const char *arm_uart_mouse_version(void);
bool arm_uart_host_usb_seen(void);
ArmHostUsbState arm_uart_host_usb_state(void);
#endif
