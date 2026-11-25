#include "abvm_vm.h"
#include "guard_runtime.h"
#include "light_sensor.h"

#include <stdio.h>
#include <stdlib.h>

static int require(int condition, const char *message) {
    if (!condition)
        fprintf(stderr, "ABVM partial dual Guard failure: %s\n", message);
    return condition;
}

bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now) {
    (void)lux_tenths;
    (void)age_ms;
    (void)now;
    return false;
}
bool light_sensor_latest_pair(LightSensorSnapshot *snapshot, uint32_t now) {
    (void)snapshot;
    (void)now;
    return false;
}

int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    rewind(file);
    uint8_t *image = malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length)
        return 2;
    fclose(file);

    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "ABP image") ||
        !require(guard_runtime_init(&vm), "v7 descriptor") ||
        !require(guard_runtime_available(), "runtime available") ||
        !require(guard_runtime_dual_sensor_enabled(), "dual mode") ||
        !require(guard_runtime_dual_calibration_mask() == 0x001u,
                 "only the captured profile starts calibrated") ||
        !require(guard_runtime_profile_pair_calibrated(1u),
                 "captured profile is enabled") ||
        !require(!guard_runtime_profile_pair_calibrated(2u),
                 "uncalibrated profile is marked pending") ||
        !require(guard_runtime_profile_matches_pair(1u, 950u, 1050u),
                 "captured profile still matches") ||
        !require(!guard_runtime_profile_matches_pair(2u, 950u, 1050u),
                 "overlapping placeholder cannot match"))
        return 1;

    /* A physical button calibration can fill one row without requiring the
     * remaining eight rows to have captured centers. */
    if (!require(guard_runtime_validate_profile_pair_center(2u, 3000u, 0),
                 "first physical calibration validates against captured rows") ||
        !require(guard_runtime_set_profile_pair_centers(2u, 3000u, 0),
                 "first physical calibration is accepted") ||
        !require(guard_runtime_dual_calibration_mask() == 0x003u,
                 "saved profile is marked calibrated") ||
        !require(guard_runtime_profile_matches_pair(2u, 1500u, 1500u),
                 "newly calibrated profile becomes matchable") ||
        !require(!guard_runtime_set_profile_pair_centers(3u, 3000u, 0),
                 "overlap with calibrated rows remains rejected"))
        return 1;

    free(image);
    puts("dual Guard: v7 partial calibration, inert placeholders, physical center update passed");
    return 0;
}