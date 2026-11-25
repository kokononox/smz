#include "abvm_vm.h"
#include "light_sensor.h"
#include "hardware/i2c.h"

#include <stdio.h>
#include <stdlib.h>

i2c_inst_t test_i2c0;
static unsigned reads;
static bool secondary_connected = true;
uint i2c_init(i2c_inst_t *i2c, uint baudrate) {
    (void)i2c; return baudrate;
}
int i2c_write_timeout_us(i2c_inst_t *i2c, uint8_t addr,
                         const uint8_t *src, size_t len, bool nostop,
                         uint timeout_us) {
    (void)i2c; (void)src; (void)nostop; (void)timeout_us;
    if (addr == 0x23u || (addr == 0x5Cu && secondary_connected))
        return (int)len;
    return -1;
}
int i2c_read_timeout_us(i2c_inst_t *i2c, uint8_t addr, uint8_t *dst,
                        size_t len, bool nostop, uint timeout_us) {
    (void)i2c; (void)nostop; (void)timeout_us;
    if (len != 2u) return -1;
    if (addr == 0x23u) {
        /* BH1750 raw 150 -> 125.0 lux. */
        dst[0] = 0u; dst[1] = 150u;
    } else if (addr == 0x5Cu && secondary_connected) {
        /* BH1750 raw 240 -> 200.0 lux, distinct from sensor one. */
        dst[0] = 0u; dst[1] = 240u;
    } else {
        return -1;
    }
    ++reads;
    return 2;
}
void gpio_set_function(uint pin, uint function) { (void)pin; (void)function; }
void gpio_pull_up(uint pin) { (void)pin; }

static int require(int condition, const char *message) {
    if (!condition) fprintf(stderr, "ABVM light smoke failure: %s\n", message);
    return condition;
}
int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    fseek(file, 0, SEEK_END); long length = ftell(file); rewind(file);
    uint8_t *image = malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length) return 2;
    fclose(file);

    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "image") ||
        !require(abvm_start_route(&vm, 10u, 0u), "Whisper route")) return 1;
    light_sensor_init(0u);
    AbvmEvent event = abvm_tick(&vm, 0u);
    if (event.type == ABVM_EVENT_RELEASE_ALL) event = abvm_tick(&vm, 0u);
    if (!require(event.type == ABVM_EVENT_ACTION, "leading key") ||
        !require(abvm_complete_action(&vm, event.lane, 0u), "complete key")) return 1;
    event = abvm_tick(&vm, 1u);
    if (!require(event.type == ABVM_EVENT_WATCH_ARMED && event.flags == 3u,
                 "Light Watch event") ||
        !require(light_sensor_arm(&vm, &event, 1u) == LIGHT_WATCH_ACCEPTED,
                 "arm BH1750")) return 1;
    light_sensor_service(&vm, 131u);
    light_sensor_service(&vm, 261u);
    light_sensor_service(&vm, 391u);
    if (!require(!light_sensor_watch_active(), "stable range detected") ||
        !require(vm.lanes[event.lane].blocked == ABVM_BLOCK_NONE,
                 "VM resumed from light")) return 1;

    uint32_t lux, age;
    if (!require(light_sensor_latest(&lux, &age, 391u) && lux == 1250u,
                 "typed latest lux")) return 1;
    LightSensorSnapshot snapshot;
    if (!require(light_sensor_latest_pair(&snapshot, 391u) &&
                 snapshot.primary_valid && snapshot.secondary_present &&
                 snapshot.secondary_valid &&
                 snapshot.primary_lux_tenths == 1250u &&
                 snapshot.secondary_lux_tenths == 2000u,
                 "paired latest lux values")) return 1;
    if (!require(light_sensor_calibration_start(300u, 400u), "start LCAL")) return 1;
    light_sensor_service(&vm, 521u);
    light_sensor_service(&vm, 651u);
    light_sensor_service(&vm, 701u);
    LightCalibrationResult result;
    if (!require(light_sensor_calibration_take(&result), "take LCAL") ||
        !require(result.valid && result.minimum_lux == 125u &&
                 result.maximum_lux == 125u && result.average_lux == 125u &&
                 result.samples >= 2u, "LCAL values")) return 1;

    if (!require(light_sensor_pair_calibration_start(650u, 700u),
                 "start paired physical calibration")) return 1;
    light_sensor_service(&vm, 781u);
    light_sensor_service(&vm, 911u);
    light_sensor_service(&vm, 1041u);
    light_sensor_service(&vm, 1171u);
    light_sensor_service(&vm, 1301u);
    light_sensor_service(&vm, 1350u);
    if (!require(light_sensor_calibration_take(&result),
                 "take paired calibration") ||
        !require(result.valid && result.dual_sensor && result.samples >= 5u &&
                 result.average_primary_tenths == 1250u &&
                 result.average_secondary_tenths == 2000u &&
                 result.mean_center_half_tenths == 3250u &&
                 result.difference_center_tenths == 750,
                 "paired M/D centers use synchronized fresh readings")) return 1;

    secondary_connected = false;
    light_sensor_init(1400u);
    light_sensor_service(&vm, 1531u);
    if (!require(light_sensor_latest_pair(&snapshot, 1531u) &&
                 snapshot.primary_valid && !snapshot.secondary_present &&
                 !snapshot.secondary_valid && light_sensor_present(),
                 "primary-only compatibility when secondary is absent") ||
        !require(!light_sensor_pair_calibration_start(500u,1532u),
                 "paired physical calibration requires both sensors")) return 1;
    free(image);
    printf("ABVM native BH1750 actor/calibration smoke passed (%u reads)\n", reads);
    return 0;
}
