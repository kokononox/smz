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
 * The sample also anchors the *wall* clock: `wake_scheduler_wall_minute()` turns
 * any later monotonic instant back into a minute of day, which is what the
 * operator logs and the flash record use.  Both the monotonic deadline and the
 * wall anchor are RAM-only; after a board reset only the persisted decision
 * inputs survive, which is why `wake_scheduler_recovery_needed()` exists.
 *
 * Only the *start* of a window is a wake target: inside a window the machine is
 * expected to be awake and running the authored round. */
typedef struct WakeScheduler {
    bool enabled;
    bool armed;
    bool manual;      /* armed by WAKE!, not by a bridge clock sample */
    bool dry;         /* a WAKE! dry arm wakes the host but starts no round */
    bool synced;      /* a bridge clock sample anchored the wall clock */
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
bool wake_scheduler_rearm(WakeScheduler *w, uint32_t now);
bool wake_scheduler_arm_at(WakeScheduler *w, uint32_t now, uint32_t in_ms);
bool wake_scheduler_arm_dry(WakeScheduler *w, uint32_t now, uint32_t in_ms);
bool wake_scheduler_wall_minute(const WakeScheduler *w, uint32_t now, uint16_t *minute);
bool wake_scheduler_recovery_needed(bool host_asleep, bool pending,
                                    uint8_t attempts, uint8_t maximum);
void wake_scheduler_disarm(WakeScheduler *w);
bool wake_scheduler_armed(const WakeScheduler *w);
bool wake_scheduler_due(const WakeScheduler *w, uint32_t now);
bool wake_scheduler_next_window_start(uint16_t minute, uint16_t day_start,
                                      uint16_t night_start, uint16_t *start);
uint16_t wake_scheduler_minutes_until(uint16_t from, uint16_t to);

#endif
