#ifndef AMS_ABVM_WAKE_SCHEDULER_H
#define AMS_ABVM_WAKE_SCHEDULER_H

#include <stdbool.h>
#include <stdint.h>

/* Deadline-based wake scheduler for the portable (hostless) build.
 *
 * The temporary Windows bridge reports the local wall clock once per shift
 * identity check.  That single sample is enough: the scheduler turns "the next
 * shift window starts at minute X" into a monotonic deadline on the Pico clock,
 * so no battery-backed RTC is required.  The deadline survives as long as the
 * Pico keeps power, which is exactly the case while the host keeps USB standby
 * power on the port the board is plugged into.
 *
 * Only the *start* of a window is a wake target: inside a window the machine is
 * expected to be awake and running the authored round. */
typedef struct WakeScheduler {
    bool enabled;
    bool armed;
    uint16_t lead_minutes;
    uint16_t day_start, day_end, night_start, night_end;
    uint16_t next_start;
    uint16_t synced_minute;
    uint32_t synced_at;
    uint32_t deadline_ms;
} WakeScheduler;

void wake_scheduler_init(WakeScheduler *w, uint16_t lead_minutes);
void wake_scheduler_configure(WakeScheduler *w, bool enabled,
                              uint16_t day_start, uint16_t day_end,
                              uint16_t night_start, uint16_t night_end);
bool wake_scheduler_sync(WakeScheduler *w, uint32_t now, uint16_t minute);
void wake_scheduler_disarm(WakeScheduler *w);
bool wake_scheduler_armed(const WakeScheduler *w);
bool wake_scheduler_due(const WakeScheduler *w, uint32_t now);
bool wake_scheduler_next_window_start(uint16_t minute, uint16_t day_start,
                                      uint16_t night_start, uint16_t *start);
uint16_t wake_scheduler_minutes_until(uint16_t from, uint16_t to);

#endif
