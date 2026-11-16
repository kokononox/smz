#include "calibration_runtime.h"
#include "calibration_store.h"
#include "guard_runtime.h"
#include "light_sensor.h"
#include <stdio.h>
#include <string.h>

#define PROFILE_COUNT 9u

static uint32_t profile_low[PROFILE_COUNT];
static uint32_t profile_high[PROFILE_COUNT];
static uint32_t stored_low[PROFILE_COUNT];
static uint32_t stored_high[PROFILE_COUNT];
static uint16_t stored_mask;
static unsigned store_calls;
static bool sample_pending;
static LightCalibrationResult sample_result;

static int require(bool condition, const char *message) {
    if (!condition) fprintf(stderr, "physical calibration tolerance failure: %s\n", message);
    return condition;
}

void calibration_store_init(const AbvmVm *vm) { (void)vm; }
bool calibration_store_light_get(uint8_t id, uint32_t *low, uint32_t *high) {
    (void)id; (void)low; (void)high; return false;
}
bool calibration_store_light_update(uint16_t mask, const uint32_t low[9],
                                     const uint32_t high[9]) {
    stored_mask = mask;
    memcpy(stored_low, low, sizeof(stored_low));
    memcpy(stored_high, high, sizeof(stored_high));
    ++store_calls;
    return true;
}
bool calibration_store_sound_set(uint16_t id, uint16_t threshold,
                                 uint16_t minimum, uint16_t silence,
                                 uint16_t peak) {
    (void)id; (void)threshold; (void)minimum; (void)silence; (void)peak;
    return true;
}
uint32_t calibration_store_revision(void) { return store_calls; }

bool guard_runtime_get_profile_range(uint8_t id, uint32_t *low, uint32_t *high) {
    if (id < 1u || id > PROFILE_COUNT || !low || !high) return false;
    *low = profile_low[id - 1u];
    *high = profile_high[id - 1u];
    return true;
}
bool guard_runtime_set_profile_range(uint8_t id, uint32_t low, uint32_t high) {
    if (id < 1u || id > PROFILE_COUNT || low > high) return false;
    profile_low[id - 1u] = low;
    profile_high[id - 1u] = high;
    return true;
}
const char *guard_runtime_profile_name(uint8_t id) {
    (void)id; return "test-profile";
}

bool light_sensor_calibration_start(uint32_t duration_ms, uint32_t now) {
    (void)duration_ms; (void)now; return true;
}
bool light_sensor_calibration_take(LightCalibrationResult *result) {
    if (!sample_pending || !result) return false;
    *result = sample_result;
    sample_pending = false;
    return true;
}
bool arm_uart_sound_calibration_start(uint32_t now, uint16_t duration_ms) {
    (void)now; (void)duration_ms; return true;
}
bool arm_uart_sound_calibration_take(uint16_t *average, uint16_t *peak) {
    (void)average; (void)peak; return false;
}

static void seed_profiles(uint32_t selected_tolerance, uint32_t neighbor_center,
                          uint32_t neighbor_tolerance) {
    for (uint8_t id = 1u; id <= PROFILE_COUNT; ++id) {
        uint32_t center = 20000u + (uint32_t)(id - 1u) * 10000u;
        uint32_t tolerance = 20u;
        if (id == 1u) { center = 20000u; tolerance = selected_tolerance; }
        if (id == 2u) { center = neighbor_center; tolerance = neighbor_tolerance; }
        profile_low[id - 1u] = center - tolerance;
        profile_high[id - 1u] = center + tolerance;
    }
    memset(stored_low, 0, sizeof(stored_low));
    memset(stored_high, 0, sizeof(stored_high));
    stored_mask = 0u;
    store_calls = 0u;
    sample_pending = false;
    memset(&sample_result, 0, sizeof(sample_result));
}

static int run_saved_floor_case(uint32_t configured_tolerance) {
    seed_profiles(configured_tolerance, 30000u, 20u);
    calibration_runtime_init(NULL);
    if (!require(calibration_runtime_blue_long(0u), "enter physical light calibration") ||
        !require(calibration_runtime_yellow_short(0u), "start physical calibration sample")) return 0;
    sample_result.valid = true;
    sample_result.minimum_lux = 2000u;
    sample_result.maximum_lux = 2000u;
    sample_result.average_lux = 2000u;
    sample_result.samples = 25u;
    sample_pending = true;
    calibration_runtime_service(5000u);
    if (!require(store_calls == 1u, "sample is persisted") ||
        !require((stored_mask & 1u) != 0u, "selected profile is persisted") ||
        !require(stored_low[0] == 20000u - configured_tolerance,
                 "low bound honors configured tolerance") ||
        !require(stored_high[0] == 20000u + configured_tolerance,
                 "high bound honors configured tolerance")) return 0;
    return 1;
}

static int run_overlap_floor_case(void) {
    /* Centers are only 3 Lux apart, so two configured ±2 Lux ranges cannot be
     * made disjoint. Calibration must fail rather than silently shrink either
     * configured tolerance below 2 Lux. */
    seed_profiles(20u, 20030u, 20u);
    calibration_runtime_init(NULL);
    if (!require(calibration_runtime_blue_long(0u), "enter overlap calibration") ||
        !require(calibration_runtime_yellow_short(0u), "start overlap sample")) return 0;
    sample_result.valid = true;
    sample_result.minimum_lux = 2000u;
    sample_result.maximum_lux = 2000u;
    sample_result.average_lux = 2000u;
    sample_result.samples = 25u;
    sample_pending = true;
    calibration_runtime_service(5000u);
    char event[192];
    if (!require(store_calls == 0u, "impossible configured ranges are not saved") ||
        !require(calibration_runtime_take_event(event, sizeof(event)), "fit error is reported") ||
        !require(strstr(event, "reason=centers-too-close") != NULL,
                 "fit reports that configured ranges cannot be separated")) return 0;
    return 1;
}

int main(void) {
    if (!run_saved_floor_case(20u)) return 1; /* configured ±2.0 Lux */
    if (!run_saved_floor_case(30u)) return 1; /* configured ±3.0 Lux */
    if (!run_overlap_floor_case()) return 1;
    puts("physical-button light calibration tolerance smoke passed");
    return 0;
}
