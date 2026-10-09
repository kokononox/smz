#ifndef AMS_GAME_BUFF_RUNTIME_H
#define AMS_GAME_BUFF_RUNTIME_H

#include <stdbool.h>
#include <stdint.h>

/* Pure Pico-side state machine. No host timer, heap, GPIO, or ARM dependency.
 * This module does not send keys: the adapter must use Pico's keyboard actor.
 * Delays and intervals must be <= INT32_MAX for rollover-safe deadlines. */
#define GAME_BUFF_MAX 16u
#define GAME_BUFF_MAX_KEYS 6u
#define GAME_BUFF_NO_INDEX 255u

typedef struct GameBuffConfig {
    bool enabled;
    uint8_t key_count;
    uint8_t keys[GAME_BUFF_MAX_KEYS]; /* Pico virtual-key combination */
    uint32_t interval_min_ms, interval_max_ms;
    uint32_t before_min_ms, before_max_ms;
    uint32_t hold_min_ms, hold_max_ms;
    uint32_t after_min_ms, after_max_ms;
} GameBuffConfig;

typedef struct GameBuffGate {
    bool game;
    bool paused;
    bool optical_priority;
    bool keyboard_free;
    /* True only before initial fishing, or after Catch AND its response finish,
     * or after Timeout. Never true while waiting for Catch. */
    bool safe_boundary;
} GameBuffGate;

typedef enum GameBuffPhase {
    GAME_BUFF_IDLE, GAME_BUFF_BEFORE, GAME_BUFF_REQUEST,
    GAME_BUFF_KEY, GAME_BUFF_AFTER
} GameBuffPhase;

typedef enum GameBuffEventKind {
    GAME_BUFF_EVENT_NONE, GAME_BUFF_EVENT_KEY_REQUEST,
    GAME_BUFF_EVENT_CONSUMED
} GameBuffEventKind;

typedef struct GameBuffEvent {
    GameBuffEventKind kind;
    uint8_t index;
    uint32_t hold_ms;
    uint32_t next_due;
} GameBuffEvent;

typedef struct GameBuffRuntime {
    GameBuffConfig config[GAME_BUFF_MAX];
    uint32_t due[GAME_BUFF_MAX], last_consumed[GAME_BUFF_MAX];
    uint32_t rng, deadline, selected_hold;
    uint16_t consumed_mask;
    uint8_t queue[GAME_BUFF_MAX], queue_count, queue_pos, count, current;
    GameBuffPhase phase;
    bool session;
} GameBuffRuntime;

bool game_buff_load(GameBuffRuntime *runtime, const uint8_t *payload, uint32_t size, uint32_t seed);
bool game_buff_init(GameBuffRuntime *runtime, const GameBuffConfig *config,
                    uint8_t count, uint32_t seed);
/* Fresh Game entry after Login/DC/Restart/Stop resets all cooldowns.
 * DO NOT call on Pause/Resume or temporary Targeted/Whisper interruptions. */
void game_buff_new_game(GameBuffRuntime *runtime);
void game_buff_end_game(GameBuffRuntime *runtime);
GameBuffEvent game_buff_service(GameBuffRuntime *runtime, uint32_t now,
                                GameBuffGate gate);
/* A request does not start cooldown. Report acceptance only after the Pico
 * actor accepted the key combination. Report completion after key release. */
bool game_buff_key_accepted(GameBuffRuntime *runtime);
bool game_buff_key_finished(GameBuffRuntime *runtime, uint32_t now);
/* If key delivery was interrupted/failed, retry the same buff at a later safe
 * boundary; never pretend that it was consumed. */
void game_buff_key_cancelled(GameBuffRuntime *runtime);
bool game_buff_pending(const GameBuffRuntime *runtime, uint32_t now);
uint32_t game_buff_remaining(const GameBuffRuntime *runtime, uint8_t index,
                             uint32_t now);
#endif