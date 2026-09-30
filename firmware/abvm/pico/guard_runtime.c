#include "guard_runtime.h"

#include <string.h>
#include "light_sensor.h"

#define GUARD_VERSION 1u
#define GUARD_PROFILE_COUNT 8u
#define GUARD_DESCRIPTOR_SIZE (8u + GUARD_PROFILE_COUNT * 20u)
#define GUARD_ROUTE_DC 5u
#define GUARD_ROUTE_RESTART 2u
#define GUARD_ROUTE_STARTUP 3u
#define GUARD_ROUTE_WHISPER 10u
#define GUARD_ROUTE_WHISPER_REPEAT 12u

typedef struct GuardProfile {
    uint8_t id;
    uint16_t route_id;
    uint32_t low;
    uint32_t high;
    uint32_t stable_ms;
    uint32_t hysteresis;
} GuardProfile;

typedef struct GuardState {
    bool available;
    bool running;
    bool paused;
    uint8_t sample_mode;
    uint8_t active;
    uint8_t candidate;
    uint8_t last_stable;
    uint8_t stage;
    bool targeted_active;
    bool whisper_light_active;
    uint16_t whisper_light_route;
    uint32_t sensor_timeout_ms;
    uint32_t candidate_since;
    uint32_t last_good_sample_at;
    GuardProfile profiles[GUARD_PROFILE_COUNT];
    GuardRuntimeEvent pending;
    bool event_pending;
} GuardState;

static GuardState guard;

static uint16_t read_u16_le(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}
static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static bool reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}
static GuardProfile *profile_by_id(uint8_t id) {
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i)
        if (guard.profiles[i].id == id) return &guard.profiles[i];
    return NULL;
}
static bool inside(const GuardProfile *profile, uint32_t lux, bool widened) {
    uint32_t low = profile->low;
    uint32_t high = profile->high;
    if (widened) {
        low = profile->hysteresis > low ? 0u : low - profile->hysteresis;
        high += profile->hysteresis;
    }
    return lux >= low && lux <= high;
}
static void emit(uint8_t type, uint8_t profile_id, uint16_t route_id,
                 uint8_t context, uint32_t lux, const char *reason) {
    guard.pending.type = type;
    guard.pending.profile_id = profile_id;
    guard.pending.stage = guard.stage;
    guard.pending.context = context;
    guard.pending.route_id = route_id;
    guard.pending.lux_tenths = lux;
    guard.pending.reason = reason;
    guard.event_pending = true;
}
static void fault(AbvmVm *vm, uint32_t now, const char *reason) {
    guard.running = false;
    emit(GUARD_EVENT_FAULT, guard.active, 0u, 0u, 0u, reason);
    abvm_stop(vm, now);
}
static void execute(AbvmVm *vm, uint8_t profile_id, uint16_t route_id,
                    uint8_t context, uint32_t lux, uint32_t now,
                    const char *reason) {
    if (!abvm_start_route(vm, route_id, now)) {
        fault(vm, now, "route-start-failed");
        return;
    }
    emit(GUARD_EVENT_ROUTE, profile_id, route_id, context, lux, reason);
}
static void transition(AbvmVm *vm, uint8_t profile_id, uint32_t lux,
                       uint32_t now) {
    GuardProfile *profile = profile_by_id(profile_id);
    if (!profile) { fault(vm, now, "profile-missing"); return; }
    if (profile_id == guard.last_stable) return;
    guard.last_stable = profile_id;

    if (!guard.stage && profile_id >= 1u && profile_id <= 5u) {
        guard.stage = profile_id;
        execute(vm, profile_id, profile->route_id,
                profile_id == 2u ? 1u : 0u, lux, now,
                "start-at-current-state");
        return;
    }
    if (profile_id == 1u) {
        emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
             "desktop-only-valid-at-start");
        return;
    }
    if (profile_id == 2u) {
        if (guard.targeted_active || guard.stage >= 3u) {
            guard.stage = 2u;
            execute(vm, profile_id, GUARD_ROUTE_DC, 2u, lux, now,
                    "dc-fallback-to-stage-2");
        } else if (guard.stage == 1u) {
            guard.stage = 2u;
            execute(vm, profile_id, profile->route_id, 1u, lux, now,
                    "stage-1-to-stage-2");
        } else emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                    "login-or-dc-not-expected");
        return;
    }
    if (profile_id == 3u || profile_id == 4u) {
        uint8_t expected = (uint8_t)(profile_id - 1u);
        if (guard.stage != expected) {
            emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                 "unexpected-ordered-stage");
            return;
        }
        guard.stage = profile_id;
        execute(vm, profile_id, profile->route_id, 0u, lux, now,
                "ordered-stage");
        return;
    }
    if (profile_id == 5u) {
        if (guard.targeted_active) {
            guard.targeted_active = false;
            emit(GUARD_EVENT_STATE, profile_id, 0u, 3u, lux,
                 "targeted-returned-to-game");
        } else if (guard.stage == 5u) {
            emit(GUARD_EVENT_STATE, profile_id, 0u, 0u, lux,
                 "game-reentry-after-unknown");
        } else if (guard.stage == 4u) {
            guard.stage = 5u;
            execute(vm, profile_id, profile->route_id, 0u, lux, now,
                    "stage-4-to-stage-5");
        } else emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                    "game-not-expected");
        return;
    }
    if (profile_id == 6u) {
        if (guard.stage == 5u && !guard.targeted_active) {
            guard.targeted_active = true;
            execute(vm, profile_id, profile->route_id, 3u, lux, now,
                    "game-to-targeted-side-state");
        } else emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                    "targeted-only-from-game");
        return;
    }
    if (profile_id == 7u || profile_id == 8u) {
        uint16_t whisper_route=profile_id==8u?
            GUARD_ROUTE_WHISPER_REPEAT:GUARD_ROUTE_WHISPER;
        if (vm->route_id == GUARD_ROUTE_WHISPER ||
            vm->route_id == GUARD_ROUTE_WHISPER_REPEAT) {
            emit(GUARD_EVENT_STATE, profile_id, 0u, 4u, lux,
                 "whisper-already-active");
        } else if (vm->route_id==GUARD_ROUTE_RESTART||
                   vm->route_id==GUARD_ROUTE_STARTUP||
                   vm->status!=ABVM_STATUS_RUNNING) {
            emit(GUARD_EVENT_DENIED,profile_id,0u,4u,lux,
                 "restart-cycle-has-priority");
            guard.active=guard.candidate=guard.last_stable=0u;
        } else {
            if (!abvm_interrupt_route(vm, whisper_route, now)) {
                fault(vm, now, "whisper-interrupt-failed");
                return;
            }
            guard.whisper_light_active = true;
            guard.whisper_light_route=whisper_route;
            emit(GUARD_EVENT_ROUTE, profile_id, whisper_route, 4u, lux,
                 profile_id==8u?"game-to-whisper-repeat-light-interrupt":
                                "game-to-whisper-new-light-interrupt");
        }
    }
}

bool guard_runtime_init(const AbvmVm *vm) {
    memset(&guard, 0, sizeof(guard));
    uint16_t constant_id; const uint8_t *payload; uint32_t size;
    if (!abvm_find_constant(vm, ABVM_CONST_GUARD, &constant_id,
                            &payload, &size)) return false;
    (void)constant_id;
    if (size != GUARD_DESCRIPTOR_SIZE || payload[0] != GUARD_VERSION ||
        payload[1] != GUARD_PROFILE_COUNT || payload[2] > 1u || payload[3])
        return false;
    guard.sample_mode = payload[2];
    guard.sensor_timeout_ms = read_u32_le(payload + 4u);
    if (guard.sensor_timeout_ms < 250u) return false;
    uint8_t seen = 0u;
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i) {
        const uint8_t *item = payload + 8u + (uint32_t)i * 20u;
        GuardProfile *profile = &guard.profiles[i];
        profile->id = item[0];
        profile->route_id = read_u16_le(item + 2u);
        profile->low = read_u32_le(item + 4u);
        profile->high = read_u32_le(item + 8u);
        profile->stable_ms = read_u32_le(item + 12u);
        profile->hysteresis = read_u32_le(item + 16u);
        if (item[1] != 1u || profile->id < 1u || profile->id > 8u ||
            (seen & (uint8_t)(1u << (profile->id - 1u))) ||
            profile->low > profile->high || !profile->route_id)
            return false;
        seen |= (uint8_t)(1u << (profile->id - 1u));
    }
    guard.available = seen == 0xffu;
    return guard.available;
}
bool guard_runtime_available(void) { return guard.available; }
bool guard_runtime_start(uint32_t now) {
    if (!guard.available) return false;
    guard.running = true;
    guard.paused = false;
    guard.active = guard.candidate = guard.last_stable = guard.stage = 0u;
    guard.targeted_active = false;
    guard.whisper_light_active = false;
    guard.whisper_light_route = 0u;
    guard.candidate_since = now;
    guard.last_good_sample_at = now;
    guard.event_pending = false;
    return true;
}
bool guard_runtime_start_after_restart(uint32_t now) {
    if (!guard_runtime_start(now)) return false;
    /* Startup owns the post-reboot desktop phase.  Resume at the last safe
     * ordered checkpoint so Desktop is intentionally skipped and the next
     * stable Login/DC profile advances stage 1 -> 2. */
    guard.stage = 1u;
    return true;
}
void guard_runtime_stop(void) {
    guard.running = false;
    guard.paused = false;
    guard.active = guard.candidate = guard.last_stable = 0u;
    guard.targeted_active = false;
    guard.whisper_light_active = false;
    guard.whisper_light_route = 0u;
}
bool guard_runtime_pause(void) {
    if (!guard.running || guard.paused) return false;
    guard.paused = true;
    return true;
}
bool guard_runtime_resume(void) {
    if (!guard.running || !guard.paused) return false;
    guard.paused = false;
    return true;
}
void guard_runtime_service(AbvmVm *vm, uint32_t now) {
    if (!guard.running || guard.paused || !vm || vm->status == ABVM_STATUS_PAUSED) return;
    if (guard.whisper_light_active) {
        if (vm->route_id == guard.whisper_light_route) return;
        guard.whisper_light_active = false;
    }
    uint32_t lux, age;
    if (light_sensor_latest(&lux, &age, now)) {
        guard.last_good_sample_at = now - age;
        guard_runtime_observe(vm, lux, now);
    } else if (reached(now, guard.last_good_sample_at + guard.sensor_timeout_ms)) {
        fault(vm, now, "sensor-timeout");
    }
}
void guard_runtime_observe(AbvmVm *vm, uint32_t lux, uint32_t now) {
    if (!guard.running || !vm) return;
    if (guard.whisper_light_active) {
        if (vm->route_id == guard.whisper_light_route) return;
        guard.whisper_light_active = false;
    }
    GuardProfile *active = profile_by_id(guard.active);
    GuardProfile *match = NULL;
    uint8_t matches = 0u;
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i)
        if (inside(&guard.profiles[i], lux, false)) {
            match = &guard.profiles[i]; ++matches;
        }
    if (matches != 1u) {
        if (active && inside(active, lux, true)) {
            guard.candidate = 0u;
            return;
        }
        bool changed = guard.active || guard.candidate;
        guard.active = guard.candidate = guard.last_stable = 0u;
        if (changed) emit(GUARD_EVENT_STATE, 0u, 0u, 0u, lux,
                          matches ? "ambiguous" : "unknown");
        return;
    }
    if (guard.active == match->id) {
        guard.candidate = 0u;
        return;
    }
    if (guard.candidate != match->id) {
        guard.candidate = match->id;
        guard.candidate_since = now;
        emit(GUARD_EVENT_STATE, match->id, 0u, 0u, lux, "candidate");
        return;
    }
    if (!reached(now, guard.candidate_since + match->stable_ms)) return;
    guard.active = match->id;
    guard.candidate = 0u;
    transition(vm, match->id, lux, now);
}
bool guard_runtime_take_event(GuardRuntimeEvent *event) {
    if (!guard.event_pending || !event) return false;
    *event = guard.pending;
    guard.event_pending = false;
    return true;
}
bool guard_runtime_running(void) { return guard.running; }
bool guard_runtime_paused(void) { return guard.paused; }
uint8_t guard_runtime_active_profile(void) { return guard.active; }
uint8_t guard_runtime_stage(void) { return guard.stage; }
const char *guard_runtime_profile_name(uint8_t profile_id) {
    static const char *names[] = {
        "unknown", "desktop", "login-or-dc", "character-dashboard",
        "entering-game-loading", "game", "targeted", "whisper-new",
        "whisper-repeat"
    };
    return profile_id <= 8u ? names[profile_id] : "invalid";
}

bool guard_runtime_get_profile_range(uint8_t profile_id,uint32_t *low_tenths,
                                     uint32_t *high_tenths) {
    GuardProfile *profile=profile_by_id(profile_id);
    if(!profile||!low_tenths||!high_tenths)return false;
    *low_tenths=profile->low;*high_tenths=profile->high;return true;
}

bool guard_runtime_set_profile_range(uint8_t profile_id,uint32_t low_tenths,uint32_t high_tenths) {
    GuardProfile *profile=profile_by_id(profile_id);
    if(!profile||low_tenths>high_tenths||high_tenths>1000000u)return false;
    profile->low=low_tenths;profile->high=high_tenths;return true;
}
