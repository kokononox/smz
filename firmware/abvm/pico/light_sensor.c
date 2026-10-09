#include "light_sensor.h"

#include <string.h>
#include "hardware/gpio.h"
#include "hardware/i2c.h"

#define LIGHT_I2C i2c0
#define LIGHT_SDA_PIN 20u
#define LIGHT_SCL_PIN 21u
#define BH1750_ADDRESS 0x23u
#define BH1750_POWER_ON 0x01u
#define BH1750_RESET 0x07u
#define BH1750_CONT_HIRES 0x10u
#define BH1750_CONT_LOWRES 0x13u
#define LIGHT_I2C_TIMEOUT_US 2500u
#define LIGHT_RETRY_MS 1000u
#define LIGHT_HIRES_INTERVAL_MS 130u
#define LIGHT_LOWRES_INTERVAL_MS 24u
#define LIGHT_FRESH_MS 1000u

typedef struct LightWatchState {
    bool active;
    bool live;
    uint8_t lane;
    uint8_t mode;
    uint16_t constant_id;
    uint32_t low_tenths;
    uint32_t high_tenths;
    uint32_t stable_ms;
    uint32_t deadline;
    uint32_t in_range_since;
    bool in_range;
} LightWatchState;

typedef struct LightCalibrationState {
    bool active;
    bool pending;
    uint32_t deadline;
    uint32_t minimum;
    uint32_t maximum;
    uint64_t total;
    uint32_t samples;
    LightCalibrationResult result;
} LightCalibrationState;

static bool sensor_present;
static bool fault_pending;
static bool sample_valid;
static uint8_t sensor_mode;
static uint32_t next_sample;
static uint32_t next_probe;
static uint32_t latest_tenths;
static uint32_t latest_at;
static LightWatchState watch_state;
static LightCalibrationState calibration;
static bool live_result_pending;
static bool live_result_detected;
static uint32_t live_result_lux_tenths;

static bool reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}
static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static uint32_t interval_for(uint8_t mode) {
    return mode ? LIGHT_LOWRES_INTERVAL_MS : LIGHT_HIRES_INTERVAL_MS;
}
static bool write_command(uint8_t command) {
    return i2c_write_timeout_us(LIGHT_I2C, BH1750_ADDRESS, &command, 1u,
                                false, LIGHT_I2C_TIMEOUT_US) == 1;
}
static bool set_mode(uint8_t mode, uint32_t now) {
    uint8_t command = mode ? BH1750_CONT_LOWRES : BH1750_CONT_HIRES;
    if (!write_command(command)) return false;
    sensor_mode = mode;
    next_sample = now + interval_for(mode);
    return true;
}
static bool probe(uint32_t now) {
    if (!write_command(BH1750_POWER_ON) || !write_command(BH1750_RESET) ||
        !set_mode(watch_state.active ? watch_state.mode : 0u, now)) {
        sensor_present = false;
        sample_valid = false;
        next_probe = now + LIGHT_RETRY_MS;
        return false;
    }
    sensor_present = true;
    return true;
}
static void lose_sensor(uint32_t now) {
    if (watch_state.active) fault_pending = true;
    sensor_present = false;
    sample_valid = false;
    next_probe = now + LIGHT_RETRY_MS;
}
static bool read_sample(uint32_t now) {
    uint8_t bytes[2];
    if (i2c_read_timeout_us(LIGHT_I2C, BH1750_ADDRESS, bytes, sizeof(bytes),
                            false, LIGHT_I2C_TIMEOUT_US) != 2) {
        lose_sensor(now);
        return false;
    }
    uint32_t raw = ((uint32_t)bytes[0] << 8) | bytes[1];
    latest_tenths = (raw * 25u + 1u) / 3u;
    latest_at = now;
    sample_valid = true;
    next_sample = now + interval_for(sensor_mode);
    return true;
}
static void calibration_sample(uint32_t value) {
    if (!calibration.active) return;
    if (!calibration.samples || value < calibration.minimum)
        calibration.minimum = value;
    if (!calibration.samples || value > calibration.maximum)
        calibration.maximum = value;
    calibration.total += value;
    ++calibration.samples;
}
static void calibration_finish(void) {
    calibration.active = false;
    calibration.pending = true;
    memset(&calibration.result, 0, sizeof(calibration.result));
    calibration.result.samples = calibration.samples;
    if (!calibration.samples) return;
    calibration.result.valid = true;
    calibration.result.minimum_lux = (calibration.minimum + 5u) / 10u;
    calibration.result.maximum_lux = (calibration.maximum + 5u) / 10u;
    calibration.result.average_lux =
        (uint32_t)((calibration.total / calibration.samples + 5u) / 10u);
}

void light_sensor_init(uint32_t now) {
    memset(&watch_state, 0, sizeof(watch_state));
    memset(&calibration, 0, sizeof(calibration));
    i2c_init(LIGHT_I2C, 100000u);
    gpio_set_function(LIGHT_SDA_PIN, GPIO_FUNC_I2C);
    gpio_set_function(LIGHT_SCL_PIN, GPIO_FUNC_I2C);
    gpio_pull_up(LIGHT_SDA_PIN);
    gpio_pull_up(LIGHT_SCL_PIN);
    sensor_present = sample_valid = fault_pending = false;
    next_probe = now;
    (void)probe(now);
}

LightWatchSubmit light_sensor_arm(const AbvmVm *vm, const AbvmEvent *event,
                                  uint32_t now) {
    if (!event || event->type != ABVM_EVENT_WATCH_ARMED || event->flags != 3u)
        return LIGHT_WATCH_UNSUPPORTED;
    if (watch_state.active) return LIGHT_WATCH_BUSY;
    const uint8_t *payload; uint32_t size;
    if (!abvm_constant(vm, event->constant_id, ABVM_CONST_LIGHT,
                       &payload, &size) || size != 16u)
        return LIGHT_WATCH_INVALID;
    uint32_t low = read_u32_le(payload);
    uint32_t high = read_u32_le(payload + 4u);
    uint32_t stable = read_u32_le(payload + 8u);
    uint8_t mode = payload[12];
    if (low > high || high > 1000000u || stable > 3600000u || mode > 1u)
        return LIGHT_WATCH_INVALID;
    if (!sensor_present && !probe(now)) return LIGHT_WATCH_NO_SENSOR;
    if (sensor_mode != mode && !set_mode(mode, now)) {
        lose_sensor(now);
        return LIGHT_WATCH_NO_SENSOR;
    }
    memset(&watch_state, 0, sizeof(watch_state));
    watch_state.active = true;
    watch_state.lane = event->lane;
    watch_state.mode = mode;
    watch_state.constant_id = event->constant_id;
    watch_state.low_tenths = low * 10u;
    watch_state.high_tenths = high * 10u;
    watch_state.stable_ms = stable;
    watch_state.deadline = now + (event->operand_b ? event->operand_b : 1u);
    return LIGHT_WATCH_ACCEPTED;
}

LightWatchSubmit light_sensor_live_start(uint32_t low_lux, uint32_t high_lux,
                                         uint32_t stable_ms, uint32_t timeout_ms,
                                         uint8_t mode, uint32_t now) {
    if(watch_state.active||calibration.active)return LIGHT_WATCH_BUSY;
    if(low_lux>high_lux||high_lux>1000000u||stable_ms>3600000u||
       !timeout_ms||mode>1u)return LIGHT_WATCH_INVALID;
    if(!sensor_present&&!probe(now))return LIGHT_WATCH_NO_SENSOR;
    if(sensor_mode!=mode&&!set_mode(mode,now)){
        lose_sensor(now);return LIGHT_WATCH_NO_SENSOR;
    }
    memset(&watch_state,0,sizeof(watch_state));
    watch_state.active=true;watch_state.live=true;watch_state.mode=mode;
    watch_state.low_tenths=low_lux*10u;
    watch_state.high_tenths=high_lux*10u;
    watch_state.stable_ms=stable_ms;watch_state.deadline=now+timeout_ms;
    live_result_pending=false;
    return LIGHT_WATCH_ACCEPTED;
}

bool light_sensor_live_take(bool *detected, uint32_t *lux_tenths) {
    if(!live_result_pending||!detected||!lux_tenths)return false;
    *detected=live_result_detected;*lux_tenths=live_result_lux_tenths;
    live_result_pending=false;return true;
}

void light_sensor_service(AbvmVm *vm, uint32_t now) {
    if (!sensor_present && reached(now, next_probe)) (void)probe(now);
    bool new_sample = false;
    if (sensor_present && reached(now, next_sample)) new_sample = read_sample(now);
    if (new_sample) calibration_sample(latest_tenths);
    if (calibration.active && reached(now, calibration.deadline)) calibration_finish();
    if (!watch_state.active) return;
    if (reached(now, watch_state.deadline)) {
        if(watch_state.live){
            live_result_pending=true;live_result_detected=false;
            live_result_lux_tenths=sample_valid?latest_tenths:0u;
        }
        light_sensor_cancel_watch(now);
        return;
    }
    if (!new_sample) return;
    bool inside = latest_tenths >= watch_state.low_tenths &&
                  latest_tenths <= watch_state.high_tenths;
    if (!inside) {
        watch_state.in_range = false;
        return;
    }
    if (!watch_state.in_range) {
        watch_state.in_range = true;
        watch_state.in_range_since = now;
    }
    if (watch_state.stable_ms &&
        !reached(now, watch_state.in_range_since + watch_state.stable_ms)) return;
    uint8_t lane = watch_state.lane;
    uint16_t constant_id = watch_state.constant_id;
    bool live=watch_state.live;
    watch_state.active = false;
    (void)set_mode(0u, now);
    if(live){
        live_result_pending=true;live_result_detected=true;
        live_result_lux_tenths=latest_tenths;return;
    }
    (void)abvm_light_detected(vm, lane, constant_id, now);
}

void light_sensor_cancel_watch(uint32_t now) {
    watch_state.active = false;
    if (sensor_present && sensor_mode != 0u && !set_mode(0u, now))
        lose_sensor(now);
}
bool light_sensor_present(void) { return sensor_present; }
bool light_sensor_latest(uint32_t *lux_tenths, uint32_t *age_ms, uint32_t now) {
    if (!sensor_present || !sample_valid || !lux_tenths || !age_ms ||
        (uint32_t)(now - latest_at) > LIGHT_FRESH_MS) return false;
    *lux_tenths = latest_tenths;
    *age_ms = now - latest_at;
    return true;
}
bool light_sensor_calibration_start(uint32_t duration_ms, uint32_t now) {
    if (!sensor_present || calibration.active || duration_ms < 100u ||
        duration_ms > 60000u) return false;
    memset(&calibration, 0, sizeof(calibration));
    calibration.active = true;
    calibration.deadline = now + duration_ms;
    return true;
}
bool light_sensor_calibration_take(LightCalibrationResult *result) {
    if (!calibration.pending || !result) return false;
    *result = calibration.result;
    calibration.pending = false;
    return true;
}
bool light_sensor_watch_active(void) { return watch_state.active; }
bool light_sensor_calibration_active(void) { return calibration.active; }
bool light_sensor_take_fault(void) {
    bool pending = fault_pending;
    fault_pending = false;
    return pending;
}
