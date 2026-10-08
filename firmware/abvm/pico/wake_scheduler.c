#include "wake_scheduler.h"

#define WAKE_MINUTE_MS 60000u
#define WAKE_MINUTES_PER_DAY 1440u

/* Strictly-after distance in minutes of day: 1..1440.  A sample taken exactly on
 * a window start therefore targets the *other* window, never itself. */
static uint16_t minutes_ahead(uint16_t from, uint16_t to) {
    uint16_t delta = (uint16_t)((to + WAKE_MINUTES_PER_DAY - from) % WAKE_MINUTES_PER_DAY);
    return delta ? delta : (uint16_t)WAKE_MINUTES_PER_DAY;
}

uint16_t wake_scheduler_minutes_until(uint16_t from, uint16_t to) {
    if (from >= WAKE_MINUTES_PER_DAY || to >= WAKE_MINUTES_PER_DAY) return 0u;
    return minutes_ahead(from, to);
}

bool wake_scheduler_next_window_start(uint16_t minute, uint16_t day_start,
                                      uint16_t night_start, uint16_t *start) {
    if (minute >= WAKE_MINUTES_PER_DAY || day_start >= WAKE_MINUTES_PER_DAY ||
        night_start >= WAKE_MINUTES_PER_DAY) return false;
    uint16_t day_ahead = minutes_ahead(minute, day_start);
    uint16_t night_ahead = minutes_ahead(minute, night_start);
    uint16_t chosen = day_ahead <= night_ahead ? day_start : night_start;
    if (start) *start = chosen;
    return true;
}

void wake_scheduler_init(WakeScheduler *w, uint16_t lead_minutes) {
    if (!w) return;
    *w = (WakeScheduler){0};
    w->lead_minutes = lead_minutes;
}

void wake_scheduler_configure(WakeScheduler *w, bool enabled,
                              uint16_t day_start, uint16_t day_end,
                              uint16_t night_start, uint16_t night_end) {
    if (!w) return;
    w->enabled = enabled;
    w->day_start = day_start;
    w->day_end = day_end;
    w->night_start = night_start;
    w->night_end = night_end;
    if (!enabled) wake_scheduler_disarm(w);
}

void wake_scheduler_disarm(WakeScheduler *w) {
    if (!w) return;
    w->armed = false;
    w->manual = false;
    w->dry = false;
    w->deadline_ms = 0u;
}

bool wake_scheduler_armed(const WakeScheduler *w) {
    /* A WAKE! test arm is honoured even when the authored schedule is off,
     * because it exists precisely to exercise the wake path on demand. */
    return w && w->armed && (w->enabled || w->manual);
}

bool wake_scheduler_sync(WakeScheduler *w, uint32_t now, uint16_t minute) {
    if (!w) return false;
    wake_scheduler_disarm(w);
    w->synced = true;
    w->synced_minute = minute;
    w->synced_at = now;
    if (!w->enabled || minute >= WAKE_MINUTES_PER_DAY) return false;
    uint16_t start = 0u;
    if (!wake_scheduler_next_window_start(minute, w->day_start, w->night_start, &start))
        return false;
    uint32_t ahead_ms = (uint32_t)minutes_ahead(minute, start) * WAKE_MINUTE_MS;
    uint32_t lead_ms = (uint32_t)w->lead_minutes * WAKE_MINUTE_MS;
    /* A window that is closer than the lead time must never produce a deadline
     * in the past: fall back to "one minute from now" and wake once. */
    uint32_t wait_ms = ahead_ms > lead_ms ? ahead_ms - lead_ms : WAKE_MINUTE_MS;
    w->next_start = start;
    w->deadline_ms = now + wait_ms;
    w->armed = true;
    return true;
}

static bool arm_manual(WakeScheduler *w, uint32_t now, uint32_t in_ms, bool dry) {
    if (!w || !in_ms) return false;
    uint32_t at = now + in_ms;
    wake_scheduler_disarm(w);
    w->armed = true;
    w->manual = true;
    w->dry = dry;
    w->deadline_ms = at;
    /* Label the test target in wall-clock terms when a sample already anchored
     * the clock, so the log reads like a real window instead of a delta. */
    uint16_t minute = 0u;
    w->next_start = wake_scheduler_wall_minute(w, at, &minute) ? minute : 0u;
    return true;
}

bool wake_scheduler_arm_at(WakeScheduler *w, uint32_t now, uint32_t in_ms) {
    return arm_manual(w, now, in_ms, false);
}

/* A dry arm proves the wake itself without starting the authored round, which is
 * what a first hardware test needs: the machine must not begin driving the
 * desktop while nobody is watching it. */
bool wake_scheduler_arm_dry(WakeScheduler *w, uint32_t now, uint32_t in_ms) {
    return arm_manual(w, now, in_ms, true);
}

bool wake_scheduler_wall_minute(const WakeScheduler *w, uint32_t now, uint16_t *minute) {
    if (!w || !w->synced) return false;
    uint32_t elapsed = (uint32_t)(now - w->synced_at);
    uint32_t value = (uint32_t)w->synced_minute + elapsed / WAKE_MINUTE_MS;
    if (minute) *minute = (uint16_t)(value % WAKE_MINUTES_PER_DAY);
    return true;
}

/* The persisted decision the board needs after a reset: the host was asleep and
 * a wake was still owed, so the board lost the only wall-clock reference it had.
 * `maximum` bounds the recovery pulses so a brownout loop cannot become a wake
 * storm; the counter is cleared by the next accepted bridge clock sample. */
bool wake_scheduler_recovery_needed(bool host_asleep, bool pending,
                                    uint8_t attempts, uint8_t maximum) {
    return host_asleep && pending && attempts < maximum;
}

bool wake_scheduler_due(const WakeScheduler *w, uint32_t now) {
    if (!wake_scheduler_armed(w)) return false;
    return (int32_t)(now - w->deadline_ms) >= 0;
}
