#ifndef AMS_SHIFT_IDENTITY_RUNTIME_H
#define AMS_SHIFT_IDENTITY_RUNTIME_H
#include <stdbool.h>
#include <stdint.h>
typedef enum ShiftKind { SHIFT_GLOBAL=0, SHIFT_DAY=1, SHIFT_NIGHT=2 } ShiftKind;
typedef enum ShiftPhase {
    SHIFT_IDLE, SHIFT_WAIT_REPLY, SHIFT_WAIT_CLOSE, SHIFT_OK, SHIFT_FAILED
} ShiftPhase;
typedef struct ShiftIdentityRuntime {
    uint8_t day_hash[32], night_hash[32], keys[4], key_count;
    uint32_t timeout_ms, hold_min, hold_max, nonce, rng, deadline, closed_at;
    ShiftKind selected, candidate;
    ShiftPhase phase;
    bool closing;
    const char *reason;
} ShiftIdentityRuntime;
bool shift_identity_load(ShiftIdentityRuntime *s, const uint8_t *data,
                         uint32_t size, uint32_t seed);
void shift_identity_begin(ShiftIdentityRuntime *s, uint32_t now);
bool shift_identity_reply(ShiftIdentityRuntime *s, uint32_t nonce,
                          const uint8_t hash[32], bool connected);
ShiftPhase shift_identity_tick(ShiftIdentityRuntime *s, uint32_t now,
                               bool connected);
void shift_identity_cancel(ShiftIdentityRuntime *s);
void shift_identity_forget(ShiftIdentityRuntime *s);
#endif