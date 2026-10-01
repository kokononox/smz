#include "guard_runtime.h"

#include <string.h>
#include "light_sensor.h"

#define GUARD_VERSION_V1 1u
#define GUARD_VERSION_V2 2u
#define GUARD_VERSION_V3 3u
#define GUARD_PROFILE_COUNT 8u
#define GUARD_PROFILE_SIZE_V1 20u
#define GUARD_PROFILE_SIZE_V2 24u
#define GUARD_ROUTE_DC 5u
#define GUARD_ROUTE_RESTART 2u
#define GUARD_ROUTE_STARTUP 3u
#define GUARD_ROUTE_WHISPER 10u
#define GUARD_ROUTE_WHISPER_REPEAT 12u
#define GUARD_ROUTE_GAME 8u
#define POST_WHISPER_TARGET_GRACE_MS 60000u

typedef struct GuardProfile {
    uint8_t id;
    uint8_t calibration_cue;
    uint16_t route_id;
    uint32_t low;
    uint32_t high;
    uint32_t stable_ms;
    uint32_t hysteresis;
    uint32_t cooldown_ms;
    uint32_t next_allowed;
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
    bool input_locked;
    bool whisper_pending;
    uint8_t whisper_pending_profile;
    uint16_t whisper_pending_route;
    uint32_t whisper_pending_lux;
    uint32_t sensor_timeout_ms;
    uint32_t candidate_since;
    uint32_t last_good_sample_at;
    uint32_t watchdog_timeout_ms;
    uint32_t stage_started_at;
    uint32_t watchdog_deadline;
    bool watchdog_enabled;
    bool watchdog_tripped;
    bool whisper_was_active;
    bool post_whisper_waiting_game;
    bool post_whisper_targeted_grace;
    uint8_t watchdog_expected_override;
    uint32_t post_whisper_targeted_since;
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
static void emit(uint8_t type,uint8_t profile_id,uint16_t route_id,
                 uint8_t context,uint32_t lux,const char *reason);
static uint8_t expected_profile(void) {
    if(guard.watchdog_expected_override)
        return guard.watchdog_expected_override;
    return guard.watchdog_enabled&&guard.stage>=1u&&guard.stage<5u?
        (uint8_t)(guard.stage+1u):0u;
}
static void clear_post_whisper_grace(void) {
    guard.post_whisper_waiting_game=false;
    guard.post_whisper_targeted_grace=false;
    guard.post_whisper_targeted_since=0u;
    guard.watchdog_expected_override=0u;
}
static void trip_operator_watchdog(AbvmVm *vm,uint32_t now,
                                   uint8_t expected,const char *reason) {
    guard.paused=true;
    guard.watchdog_tripped=true;
    guard.watchdog_expected_override=expected;
    if(vm->status==ABVM_STATUS_RUNNING)(void)abvm_pause(vm,now);
    emit(GUARD_EVENT_WATCHDOG_TRIPPED,expected,0u,0u,0u,reason);
}
static void watchdog_advance(uint32_t now) {
    if(!guard.watchdog_enabled)return;
    if(guard.stage>=5u) {
        guard.watchdog_enabled=false;
        guard.watchdog_deadline=0u;
        return;
    }
    guard.stage_started_at=now;
    guard.watchdog_deadline=now+guard.watchdog_timeout_ms;
}
static GuardProfile *profile_by_id(uint8_t id);
static void arm_light_whisper_cooldown(uint8_t profile_id,uint32_t now) {
    GuardProfile *profile=profile_by_id(profile_id);
    if(profile&&profile->cooldown_ms)
        profile->next_allowed=now+profile->cooldown_ms;
}
static GuardProfile *profile_by_id(uint8_t id) {
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i)
        if (guard.profiles[i].id == id) return &guard.profiles[i];
    return NULL;
}
static bool whisper_route_active(const AbvmVm *vm) {
    return vm && vm->status == ABVM_STATUS_RUNNING &&
           (vm->route_id == GUARD_ROUTE_WHISPER ||
            vm->route_id == GUARD_ROUTE_WHISPER_REPEAT);
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
static GuardProfile *unique_match(uint32_t lux, uint8_t *matches_out) {
    GuardProfile *match = NULL;
    uint8_t matches = 0u;
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i)
        if (inside(&guard.profiles[i], lux, false)) {
            match = &guard.profiles[i];
            ++matches;
        }
    if (matches_out) *matches_out = matches;
    return matches == 1u ? match : NULL;
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
    if(guard.whisper_pending&&profile_id!=guard.whisper_pending_profile)
        guard.whisper_pending=false;

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
        clear_post_whisper_grace();
        guard.whisper_was_active=false;
        if (guard.targeted_active || guard.stage >= 3u) {
            guard.stage = 2u;
            watchdog_advance(now);
            execute(vm, profile_id, GUARD_ROUTE_DC, 2u, lux, now,
                    "dc-fallback-to-stage-2");
        } else if (guard.stage == 1u) {
            guard.stage = 2u;
            watchdog_advance(now);
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
        watchdog_advance(now);
        execute(vm, profile_id, profile->route_id, 0u, lux, now,
                "ordered-stage");
        return;
    }
    if (profile_id == 5u) {
        clear_post_whisper_grace();
        if (guard.targeted_active) {
            guard.targeted_active = false;
            emit(GUARD_EVENT_STATE, profile_id, 0u, 3u, lux,
                 "targeted-returned-to-game");
        } else if (guard.stage == 5u) {
            emit(GUARD_EVENT_STATE, profile_id, 0u, 0u, lux,
                 "game-reentry-after-unknown");
        } else if (guard.stage == 4u) {
            guard.stage = 5u;
            watchdog_advance(now);
            execute(vm, profile_id, profile->route_id, 0u, lux, now,
                    "stage-4-to-stage-5");
        } else emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                    "game-not-expected");
        return;
    }
    if (profile_id == 6u) {
        if(guard.stage==5u&&guard.post_whisper_waiting_game) {
            guard.post_whisper_targeted_grace=true;
            guard.post_whisper_targeted_since=now;
            guard.watchdog_expected_override=5u;
            emit(GUARD_EVENT_STATE,profile_id,0u,3u,lux,
                 "post-whisper-targeted-grace-start");
        } else if (guard.stage == 5u && !guard.targeted_active) {
            if (!abvm_interrupt_route(vm, profile->route_id, now)) {
                fault(vm, now, "targeted-interrupt-failed");
                return;
            }
            guard.targeted_active = true;
            emit(GUARD_EVENT_ROUTE, profile_id, profile->route_id, 3u, lux,
                 "game-to-targeted-interrupt");
        } else emit(GUARD_EVENT_DENIED, profile_id, 0u, 0u, lux,
                    "targeted-only-from-game");
        return;
    }
    if (profile_id == 7u || profile_id == 8u) {
        uint16_t whisper_route=profile_id==8u?
            GUARD_ROUTE_WHISPER_REPEAT:GUARD_ROUTE_WHISPER;
        if(profile->next_allowed&&!reached(now,profile->next_allowed)) {
            emit(GUARD_EVENT_DENIED,profile_id,0u,4u,lux,
                 "light-whisper-cooldown");
        } else if (vm->route_id == GUARD_ROUTE_WHISPER ||
            vm->route_id == GUARD_ROUTE_WHISPER_REPEAT) {
            emit(GUARD_EVENT_STATE, profile_id, 0u, 4u, lux,
                 "whisper-already-active");
        } else if (vm->route_id==GUARD_ROUTE_RESTART||
                   vm->route_id==GUARD_ROUTE_STARTUP||
                   vm->status!=ABVM_STATUS_RUNNING) {
            emit(GUARD_EVENT_DENIED,profile_id,0u,4u,lux,
                 "restart-cycle-has-priority");
            guard.active=guard.candidate=guard.last_stable=0u;
        } else if(guard.input_locked) {
            guard.whisper_pending=true;
            guard.whisper_pending_profile=profile_id;
            guard.whisper_pending_route=whisper_route;
            guard.whisper_pending_lux=lux;
            emit(GUARD_EVENT_STATE,profile_id,0u,4u,lux,
                 "whisper-waiting-input-release");
        } else {
            if (!abvm_interrupt_route(vm, whisper_route, now)) {
                fault(vm, now, "whisper-interrupt-failed");
                return;
            }
            guard.whisper_light_active = true;
            guard.whisper_light_route=whisper_route;
            arm_light_whisper_cooldown(profile_id,now);
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
    uint8_t version=payload[0];
    uint32_t profile_size=(version==GUARD_VERSION_V2||version==GUARD_VERSION_V3)?
        GUARD_PROFILE_SIZE_V2:GUARD_PROFILE_SIZE_V1;
    if ((version!=GUARD_VERSION_V1&&version!=GUARD_VERSION_V2&&
         version!=GUARD_VERSION_V3) ||
        size != 8u + GUARD_PROFILE_COUNT * profile_size ||
        payload[1] != GUARD_PROFILE_COUNT || payload[2] > 1u ||
        (version<GUARD_VERSION_V3&&payload[3]) ||
        (version==GUARD_VERSION_V3&&(!payload[3]||payload[3]>60u)))
        return false;
    guard.sample_mode = payload[2];
    guard.watchdog_timeout_ms=version==GUARD_VERSION_V3?
        (uint32_t)payload[3]*60000u:0u;
    guard.sensor_timeout_ms = read_u32_le(payload + 4u);
    if (guard.sensor_timeout_ms < 250u) return false;
    uint8_t seen = 0u;
    for (uint8_t i = 0; i < GUARD_PROFILE_COUNT; ++i) {
        const uint8_t *item = payload + 8u + (uint32_t)i * profile_size;
        GuardProfile *profile = &guard.profiles[i];
        profile->id = item[0];
        profile->calibration_cue=version>=GUARD_VERSION_V2?item[1]:item[0];
        profile->route_id = read_u16_le(item + 2u);
        profile->low = read_u32_le(item + 4u);
        profile->high = read_u32_le(item + 8u);
        profile->stable_ms = read_u32_le(item + 12u);
        profile->hysteresis = read_u32_le(item + 16u);
        profile->cooldown_ms=version>=GUARD_VERSION_V2?
            read_u32_le(item+20u):0u;
        profile->next_allowed=0u;
        if ((version==GUARD_VERSION_V1&&item[1]!=1u) ||
            profile->calibration_cue<1u||profile->calibration_cue>8u ||
            profile->id < 1u || profile->id > 8u ||
            (seen & (uint8_t)(1u << (profile->id - 1u))) ||
            profile->low > profile->high || !profile->route_id ||
            profile->cooldown_ms>3600000u ||
            (profile->id!=7u&&profile->id!=8u&&profile->cooldown_ms))
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
    guard.whisper_pending=false;
    guard.candidate_since = now;
    guard.last_good_sample_at = now;
    guard.event_pending = false;
    guard.watchdog_enabled=false;
    guard.watchdog_tripped=false;
    guard.watchdog_deadline=0u;
    guard.whisper_was_active=false;
    clear_post_whisper_grace();
    guard.stage_started_at=now;
    for(uint8_t i=0;i<GUARD_PROFILE_COUNT;++i)
        guard.profiles[i].next_allowed=0u;
    return true;
}
bool guard_runtime_start_after_restart(uint32_t now) {
    if (!guard_runtime_start(now)) return false;
    /* Startup owns the post-reboot desktop phase.  Resume at the last safe
     * ordered checkpoint so Desktop is intentionally skipped and the next
     * stable Login/DC profile advances stage 1 -> 2. */
    guard.stage = 1u;
    if(guard.watchdog_timeout_ms) {
        guard.watchdog_enabled=true;
        guard.stage_started_at=now;
        guard.watchdog_deadline=now+guard.watchdog_timeout_ms;
    }
    return true;
}
void guard_runtime_stop(void) {
    guard.running = false;
    guard.paused = false;
    guard.active = guard.candidate = guard.last_stable = 0u;
    guard.targeted_active = false;
    guard.whisper_light_active = false;
    guard.whisper_light_route = 0u;
    guard.whisper_pending=false;
    guard.watchdog_enabled=false;
    guard.watchdog_tripped=false;
    guard.watchdog_deadline=0u;
    guard.whisper_was_active=false;
    clear_post_whisper_grace();
}
bool guard_runtime_pause(void) {
    if (!guard.running || guard.paused) return false;
    guard.paused = true;
    return true;
}
bool guard_runtime_resume(void) {
    if (!guard.running || !guard.paused) return false;
    guard.paused = false;
    if(guard.watchdog_tripped) {
        guard.watchdog_tripped=false;
        guard.stage_started_at=0u;
        guard.watchdog_deadline=0u;
        guard.candidate=0u;
        if(guard.watchdog_expected_override==5u) {
            guard.post_whisper_waiting_game=true;
            guard.post_whisper_targeted_grace=false;
            guard.post_whisper_targeted_since=0u;
            guard.active=guard.last_stable=0u;
        }
    }
    return true;
}
void guard_runtime_service(AbvmVm *vm, uint32_t now) {
    if (!guard.running || guard.paused || !vm || vm->status == ABVM_STATUS_PAUSED) return;
    if(guard.watchdog_enabled) {
        if(!guard.watchdog_deadline) {
            guard.stage_started_at=now;
            guard.watchdog_deadline=now+guard.watchdog_timeout_ms;
        } else if(reached(now,guard.watchdog_deadline)) {
            trip_operator_watchdog(vm,now,expected_profile(),
                                   "expected-stage-timeout");
            return;
        }
    }
    if(guard.whisper_pending) {
        if(vm->route_id==GUARD_ROUTE_RESTART||
           vm->route_id==GUARD_ROUTE_STARTUP||
           vm->status!=ABVM_STATUS_RUNNING) {
            guard.whisper_pending=false;
            return;
        }
        if(guard.input_locked)return;
        if(!abvm_interrupt_route(vm,guard.whisper_pending_route,now)) {
            fault(vm,now,"whisper-deferred-interrupt-failed");
            return;
        }
        guard.whisper_light_active=true;
        guard.whisper_light_route=guard.whisper_pending_route;
        arm_light_whisper_cooldown(guard.whisper_pending_profile,now);
        emit(GUARD_EVENT_ROUTE,guard.whisper_pending_profile,
             guard.whisper_pending_route,4u,guard.whisper_pending_lux,
             guard.whisper_pending_profile==8u?
             "deferred-whisper-repeat-after-input-release":
             "deferred-whisper-new-after-input-release");
        guard.whisper_pending=false;
        return;
    }
    /*
     * Whisper New/Repeat describe transient overlays, not a durable optical
     * scene.  While either bounded interrupt is running, ignore every Guard
     * sample (including an apparent Desktop return) and let every Whisper
     * step finish.  This applies equally to light-, sound-, and manual-origin
     * interrupts.  The current sensor state is evaluated again only after the
     * VM restores the suspended route.
     */
    if (whisper_route_active(vm)) {
        guard.whisper_was_active=true;
        return;
    }
    if(guard.whisper_was_active) {
        guard.whisper_was_active=false;
        if(vm->status==ABVM_STATUS_RUNNING&&vm->route_id==GUARD_ROUTE_GAME) {
            guard.post_whisper_waiting_game=true;
            guard.watchdog_expected_override=5u;
        }
    }
    if (guard.whisper_light_active) {
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
void guard_runtime_set_input_locked(bool locked){guard.input_locked=locked;}
void guard_runtime_observe(AbvmVm *vm, uint32_t lux, uint32_t now) {
    if (!guard.running || !vm) return;
    if (whisper_route_active(vm)) {
        guard.whisper_was_active=true;
        /*
         * A stable Login/DC scene is the sole exception to Whisper's optical
         * lock: recovery must replace the transient overlay immediately.
         * Desktop and every other scene remain ignored until Whisper ends.
         */
        uint8_t whisper_matches = 0u;
        GuardProfile *whisper_match = unique_match(lux, &whisper_matches);
        if (whisper_matches != 1u || !whisper_match ||
            whisper_match->id != 2u) {
            if (guard.candidate == 2u) guard.candidate = 0u;
            return;
        }
        if (guard.active == 2u) {
            guard.candidate = 0u;
            return;
        }
        if (guard.candidate != 2u) {
            guard.candidate = 2u;
            guard.candidate_since = now;
            emit(GUARD_EVENT_STATE, 2u, 0u, 0u, lux,
                 "dc-candidate-during-whisper");
            return;
        }
        if (!reached(now, guard.candidate_since +
                     whisper_match->stable_ms)) return;
        guard.active = 2u;
        guard.candidate = 0u;
        transition(vm, 2u, lux, now);
        return;
    }
    if (guard.whisper_light_active) {
        guard.whisper_light_active = false;
    }
    GuardProfile *active = profile_by_id(guard.active);
    uint8_t matches = 0u;
    GuardProfile *match = unique_match(lux, &matches);
    if (matches != 1u) {
        if(guard.post_whisper_targeted_grace) {
            guard.post_whisper_targeted_grace=false;
            guard.post_whisper_targeted_since=0u;
            guard.post_whisper_waiting_game=true;
        }
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
    if(guard.post_whisper_targeted_grace) {
        if(match->id==6u) {
            if(reached(now,guard.post_whisper_targeted_since+
                      POST_WHISPER_TARGET_GRACE_MS))
                trip_operator_watchdog(vm,now,5u,
                                       "post-whisper-targeted-timeout");
            return;
        }
        guard.post_whisper_targeted_grace=false;
        guard.post_whisper_targeted_since=0u;
        guard.post_whisper_waiting_game=true;
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
bool guard_runtime_watchdog_tripped(void) { return guard.watchdog_tripped; }
uint8_t guard_runtime_active_profile(void) { return guard.active; }
uint8_t guard_runtime_stage(void) { return guard.stage; }
uint8_t guard_runtime_expected_profile(void) { return expected_profile(); }
uint32_t guard_runtime_stage_elapsed(uint32_t now) {
    if(guard.watchdog_expected_override==5u&&
       guard.post_whisper_targeted_since)
        return now-guard.post_whisper_targeted_since;
    return guard.watchdog_enabled?now-guard.stage_started_at:0u;
}
uint32_t guard_runtime_watchdog_timeout_ms(void) {
    if(guard.watchdog_expected_override==5u)
        return POST_WHISPER_TARGET_GRACE_MS;
    return guard.watchdog_timeout_ms;
}
uint8_t guard_runtime_calibration_cue(uint8_t profile_id) {
    GuardProfile *profile=profile_by_id(profile_id);
    return profile?profile->calibration_cue:profile_id;
}
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
