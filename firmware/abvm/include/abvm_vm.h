#ifndef AMS_ABVM_VM_H
#define AMS_ABVM_VM_H

#include <stddef.h>
#include <stdint.h>

#include "abvm_abi1.h"

#ifdef __cplusplus
extern "C" {
#endif

#define ABVM_MAX_FRAMES 8u
#define ABVM_MAX_LANES 2u
#define ABVM_MAX_PACKAGE_ITEMS ABVM_LIMIT_PACKAGE_ITEMS

typedef enum AbvmStatus {
    ABVM_STATUS_IDLE = 0,
    ABVM_STATUS_RUNNING = 1,
    ABVM_STATUS_PAUSED = 2,
    ABVM_STATUS_STOPPED = 3,
    ABVM_STATUS_COMPLETE = 4,
    ABVM_STATUS_FAULT = 5,
} AbvmStatus;

typedef enum AbvmEventType {
    ABVM_EVENT_NONE = 0,
    ABVM_EVENT_ACTION = 1,
    ABVM_EVENT_WATCH_ARMED = 2,
    ABVM_EVENT_ROUTE_COMPLETE = 3,
    ABVM_EVENT_INTERRUPT_RESUME = 4,
    ABVM_EVENT_RELEASE_ALL = 5,
    ABVM_EVENT_FAULT = 6,
} AbvmEventType;

typedef enum AbvmFrameType {
    ABVM_FRAME_LOOP = 1,
    ABVM_FRAME_PACKAGE = 2,
} AbvmFrameType;

typedef enum AbvmBlockType {
    ABVM_BLOCK_NONE = 0,
    ABVM_BLOCK_ACTION = 1,
    ABVM_BLOCK_WATCH = 2,
} AbvmBlockType;

typedef struct AbvmFrame {
    uint8_t type;
    uint8_t mode;
    uint16_t constant_id;
    uint32_t body_pc;
    uint32_t after_pc;
    uint32_t remaining;
    uint32_t deadline;
    uint32_t selected_mask;
    uint8_t next_item;
    uint8_t item_count;
    uint16_t reserved;
} AbvmFrame;

typedef struct AbvmLane {
    uint32_t pc;
    uint32_t end_pc;
    uint32_t due;
    uint32_t watch_after_pc;
    uint32_t watch_deadline;
    uint16_t watch_profile;
    uint8_t frame_count;
    uint8_t active;
    uint8_t terminal;
    uint8_t blocked;
    uint16_t reserved;
    AbvmFrame frames[ABVM_MAX_FRAMES];
} AbvmLane;

typedef struct AbvmScopeState {
    uint8_t active;
    uint8_t policy;
    uint8_t terminal_lane;
    uint8_t parent_lane;
    uint8_t parent_frame_count;
    uint8_t reserved[3];
    uint32_t parent_pc;
    uint32_t parent_end_pc;
} AbvmScopeState;

typedef struct AbvmContext {
    uint8_t valid;
    uint8_t lane_count;
    uint16_t route_flags;
    uint16_t route_id;
    uint16_t reserved;
    AbvmLane lanes[ABVM_MAX_LANES];
    AbvmScopeState scope;
} AbvmContext;

typedef struct AbvmEvent {
    uint8_t type;
    uint8_t opcode;
    uint8_t lane;
    uint8_t reserved;
    uint16_t operand_a;
    uint16_t route_id;
    uint32_t operand_b;
    uint32_t operand_c;
    uint32_t operand_d;
    const char *message;
} AbvmEvent;

typedef struct AbvmVm {
    const uint8_t *image;
    size_t image_size;
    AbvmHeader header;
    AbvmResourceCertificate resources;
    AbvmLane lanes[ABVM_MAX_LANES];
    AbvmScopeState scope;
    AbvmContext suspended;
    uint32_t now;
    uint32_t paused_at;
    uint32_t prng;
    uint16_t route_id;
    uint16_t route_flags;
    uint8_t lane_count;
    uint8_t status;
    uint8_t pending_release;
    uint8_t reserved;
    const char *fault;
} AbvmVm;

int abvm_init(AbvmVm *vm, const uint8_t *image, size_t image_size);
int abvm_start_route(AbvmVm *vm, uint16_t route_id, uint32_t now);
int abvm_interrupt_route(AbvmVm *vm, uint16_t route_id, uint32_t now);
int abvm_pause(AbvmVm *vm, uint32_t now);
int abvm_resume(AbvmVm *vm, uint32_t now);
void abvm_stop(AbvmVm *vm, uint32_t now);
AbvmEvent abvm_tick(AbvmVm *vm, uint32_t now);
int abvm_complete_action(AbvmVm *vm, uint8_t lane, uint32_t now);
int abvm_sound_detected(AbvmVm *vm, uint16_t profile, uint32_t now);
int abvm_constant(const AbvmVm *vm, uint16_t constant_id, uint8_t kind,
                  const uint8_t **payload, uint32_t *size);
const char *abvm_status_name(uint8_t status);

#ifdef __cplusplus
}
#endif

#endif