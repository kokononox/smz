#include "game_buff_runtime.h"
#include <limits.h>
#include <string.h>

static uint32_t le32(const uint8_t *p) {
    return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);
}
bool game_buff_load(GameBuffRuntime *r, const uint8_t *p, uint32_t size, uint32_t seed) {
    if (!p || size < 8u || memcmp(p,"GBF1",4u) || p[4] != 1u ||
        !p[5] || p[5]>GAME_BUFF_MAX || p[6] || p[7] || size != 8u+(uint32_t)p[5]*40u) return false;
    GameBuffConfig c[GAME_BUFF_MAX] = {0};
    for (uint8_t i=0u;i<p[5];++i) {
        const uint8_t *row=p+8u+(uint32_t)i*40u;
        c[i].enabled=true;c[i].key_count=row[4];
        if (!row[4] || row[4]>4u || row[5] || row[6] || row[7]) return false;
        memcpy(c[i].keys,row,row[4]);
        for (uint8_t k=row[4];k<4u;++k) if(row[k]) return false;
        c[i].interval_min_ms=le32(row+8u);c[i].interval_max_ms=le32(row+12u);
        c[i].before_min_ms=le32(row+16u);c[i].before_max_ms=le32(row+20u);
        c[i].hold_min_ms=le32(row+24u);c[i].hold_max_ms=le32(row+28u);
        c[i].after_min_ms=le32(row+32u);c[i].after_max_ms=le32(row+36u);
    }
    return game_buff_init(r,c,p[5],seed);
}
static bool reached(uint32_t now, uint32_t deadline) {
    return (int32_t)(now - deadline) >= 0;
}
static bool range_valid(uint32_t lo, uint32_t hi, bool positive) {
    return lo <= hi && hi <= INT32_MAX && (!positive || lo > 0u);
}
static uint32_t random_next(GameBuffRuntime *r) {
    uint32_t x = r->rng;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    r->rng = x;
    return x;
}
/* Rejection sampling avoids modulo bias for both shuffle and intervals. */
static uint32_t bounded(GameBuffRuntime *r, uint32_t bound) {
    uint32_t threshold = (uint32_t)(0u - bound) % bound;
    uint32_t value;
    do { value = random_next(r); } while (value < threshold);
    return value % bound;
}
static uint32_t between(GameBuffRuntime *r, uint32_t lo, uint32_t hi) {
    return lo + bounded(r, hi - lo + 1u);
}
static bool due(const GameBuffRuntime *r, uint8_t index, uint32_t now) {
    return r->config[index].enabled &&
        (!(r->consumed_mask & (uint16_t)(1u << index)) ||
         reached(now, r->due[index]));
}
static bool allowed(GameBuffGate gate) {
    return gate.game && !gate.paused && !gate.optical_priority &&
           gate.keyboard_free && gate.safe_boundary;
}
static void batch(GameBuffRuntime *r, uint32_t now) {
    r->queue_count = r->queue_pos = 0u;
    for (uint8_t i = 0u; i < r->count; ++i)
        if (due(r, i, now)) r->queue[r->queue_count++] = i;
    for (uint8_t i = r->queue_count; i > 1u; --i) {
        uint8_t j = (uint8_t)bounded(r, i);
        uint8_t temp = r->queue[i - 1u];
        r->queue[i - 1u] = r->queue[j]; r->queue[j] = temp;
    }
}
bool game_buff_init(GameBuffRuntime *r, const GameBuffConfig *config,
                    uint8_t count, uint32_t seed) {
    if (!r) return false;
    memset(r, 0, sizeof(*r));
    r->current = GAME_BUFF_NO_INDEX;
    if (count > GAME_BUFF_MAX || (count && !config)) return false;
    for (uint8_t i = 0u; i < count; ++i) {
        const GameBuffConfig *c = &config[i];
        if (!c->enabled) continue;
        if (!c->key_count || c->key_count > GAME_BUFF_MAX_KEYS ||
            !range_valid(c->interval_min_ms, c->interval_max_ms, true) ||
            !range_valid(c->before_min_ms, c->before_max_ms, false) ||
            !range_valid(c->hold_min_ms, c->hold_max_ms, true) ||
            !range_valid(c->after_min_ms, c->after_max_ms, false)) return false;
        for (uint8_t k = 0u; k < c->key_count; ++k) {
            if (!c->keys[k]) return false;
            for (uint8_t j = 0u; j < k; ++j)
                if (c->keys[k] == c->keys[j]) return false;
        }
    }
    if (count) memcpy(r->config, config, count * sizeof(*config));
    r->count = count;
    r->rng = seed ? seed : 0xa341316cu;
    return true;
}
void game_buff_new_game(GameBuffRuntime *r) {
    if (!r) return;
    r->consumed_mask = 0u;
    memset(r->due, 0, sizeof(r->due));
    memset(r->last_consumed, 0, sizeof(r->last_consumed));
    r->queue_count = r->queue_pos = 0u;
    r->current = GAME_BUFF_NO_INDEX;
    r->phase = GAME_BUFF_IDLE;
    r->session = true;
    /* Keep RNG state across runs, so new Game entries aren't identical. */
}
void game_buff_end_game(GameBuffRuntime *r) {
    if (!r) return;
    r->session = false;
    r->phase = GAME_BUFF_IDLE;
    r->queue_count = r->queue_pos = 0u;
    r->current = GAME_BUFF_NO_INDEX;
}
GameBuffEvent game_buff_service(GameBuffRuntime *r, uint32_t now,
                                GameBuffGate gate) {
    GameBuffEvent event = {GAME_BUFF_EVENT_NONE, GAME_BUFF_NO_INDEX, 0u, 0u};
    if (!r || !r->session) return event;
    if (r->phase == GAME_BUFF_AFTER && reached(now, r->deadline)) {
        uint8_t index = r->current;
        const GameBuffConfig *c = &r->config[index];
        /* End-of-consumption deadline, NOT a late service call: Pause must not
         * move the cooldown origin forward. No key is sent in this branch. */
        r->last_consumed[index] = r->deadline;
        r->due[index] = r->deadline +
            between(r, c->interval_min_ms, c->interval_max_ms);
        r->consumed_mask |= (uint16_t)(1u << index);
        r->phase = GAME_BUFF_IDLE;
        r->current = GAME_BUFF_NO_INDEX;
        ++r->queue_pos;
        event.kind = GAME_BUFF_EVENT_CONSUMED;
        event.index = index;
        event.next_due = r->due[index];
        return event;
    }
    if (!allowed(gate)) return event;
    if (r->phase == GAME_BUFF_IDLE) {
        if (r->queue_pos >= r->queue_count) batch(r, now);
        if (!r->queue_count) return event;
        r->current = r->queue[r->queue_pos];
        const GameBuffConfig *c = &r->config[r->current];
        r->deadline = now + between(r, c->before_min_ms, c->before_max_ms);
        r->selected_hold = between(r, c->hold_min_ms, c->hold_max_ms);
        r->phase = GAME_BUFF_BEFORE;
    }
    if (r->phase == GAME_BUFF_BEFORE && reached(now, r->deadline))
        r->phase = GAME_BUFF_REQUEST;
    if (r->phase == GAME_BUFF_REQUEST) {
        event.kind = GAME_BUFF_EVENT_KEY_REQUEST;
        event.index = r->current;
        event.hold_ms = r->selected_hold;
    }
    return event;
}
bool game_buff_key_accepted(GameBuffRuntime *r) {
    if (!r || !r->session || r->phase != GAME_BUFF_REQUEST) return false;
    r->phase = GAME_BUFF_KEY;
    return true;
}
bool game_buff_key_finished(GameBuffRuntime *r, uint32_t now) {
    if (!r || !r->session || r->phase != GAME_BUFF_KEY) return false;
    const GameBuffConfig *c = &r->config[r->current];
    r->deadline = now + between(r, c->after_min_ms, c->after_max_ms);
    r->phase = GAME_BUFF_AFTER;
    return true;
}
void game_buff_key_cancelled(GameBuffRuntime *r) {
    if (!r || r->phase != GAME_BUFF_KEY) return;
    r->phase = GAME_BUFF_REQUEST;
}
bool game_buff_pending(const GameBuffRuntime *r, uint32_t now) {
    if (!r || !r->session) return false;
    if (r->phase != GAME_BUFF_IDLE || r->queue_pos < r->queue_count) return true;
    for (uint8_t i = 0u; i < r->count; ++i) if (due(r, i, now)) return true;
    return false;
}
uint32_t game_buff_remaining(const GameBuffRuntime *r, uint8_t index,
                             uint32_t now) {
    if (!r || !r->session || index >= r->count ||
        !r->config[index].enabled || due(r, index, now)) return 0u;
    return r->due[index] - now;
}