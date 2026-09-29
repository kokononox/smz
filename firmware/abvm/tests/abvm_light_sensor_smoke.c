#include "abvm_vm.h"
#include "light_sensor.h"
#include "hardware/i2c.h"

#include <stdio.h>
#include <stdlib.h>

i2c_inst_t test_i2c0;
static unsigned reads;
uint i2c_init(i2c_inst_t *i2c, uint baudrate) {
    (void)i2c; return baudrate;
}
int i2c_write_timeout_us(i2c_inst_t *i2c, uint8_t addr,
                         const uint8_t *src, size_t len, bool nostop,
                         uint timeout_us) {
    (void)i2c; (void)addr; (void)src; (void)nostop; (void)timeout_us;
    return (int)len;
}
int i2c_read_timeout_us(i2c_inst_t *i2c, uint8_t addr, uint8_t *dst,
                        size_t len, bool nostop, uint timeout_us) {
    (void)i2c; (void)addr; (void)nostop; (void)timeout_us;
    if (len != 2u) return -1;
    /* BH1750 raw 150 -> 125.0 lux. */
    dst[0] = 0u; dst[1] = 150u; ++reads; return 2;
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
    if (!require(light_sensor_calibration_start(300u, 400u), "start LCAL")) return 1;
    light_sensor_service(&vm, 521u);
    light_sensor_service(&vm, 651u);
    light_sensor_service(&vm, 701u);
    LightCalibrationResult result;
    if (!require(light_sensor_calibration_take(&result), "take LCAL") ||
        !require(result.valid && result.minimum_lux == 125u &&
                 result.maximum_lux == 125u && result.average_lux == 125u &&
                 result.samples >= 2u, "LCAL values")) return 1;
    free(image);
    printf("ABVM native BH1750 actor/calibration smoke passed (%u reads)\n", reads);
    return 0;
}
