#include "light_sensor.h"

#include <string.h>
#include "hardware/gpio.h"
#include "hardware/i2c.h"

#define LIGHT_I2C i2c0
#define LIGHT_SDA_PIN 20u
#define LIGHT_SCL_PIN 21u
#define BH1750_PRIMARY_ADDRESS 0x23u
#define BH1750_SECONDARY_ADDRESS 0x5Cu
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
    bool dual_sensor;
    bool invalid;
    bool pair_sampled;
    uint32_t deadline;
    uint32_t started_at;
    uint32_t minimum;
    uint32_t maximum;
    uint64_t total;
    uint32_t samples;
    uint64_t primary_total;
    uint64_t secondary_total;
    uint64_t mean_total;
    int64_t difference_total;
    uint32_t last_primary_at;
    uint32_t last_secondary_at;
    LightCalibrationResult result;
} LightCalibrationState;

static bool sensor_present;
static bool secondary_sensor_present;
static bool fault_pending;
static bool sample_valid;
static bool secondary_sample_valid;
static uint8_t sensor_mode;
static uint32_t next_sample;
static uint32_t next_probe;
static uint32_t latest_tenths;
static uint32_t latest_at;
static uint32_t secondary_next_sample;
static uint32_t secondary_next_probe;
static uint32_t secondary_latest_tenths;
static uint32_t secondary_latest_at;
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
static bool write_command_to(uint8_t address, uint8_t command) {
    return i2c_write_timeout_us(LIGHT_I2C, address, &command, 1u,
                                false, LIGHT_I2C_TIMEOUT_US) == 1;
}
static bool write_command(uint8_t command) {
    return write_command_to(BH1750_PRIMARY_ADDRESS, command);
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
    if (calibration.active && calibration.dual_sensor) calibration.invalid = true;
    sensor_present = false;
    sample_valid = false;
    next_probe = now + LIGHT_RETRY_MS;
}
static bool probe_secondary(uint32_t now) {
    if (!write_command_to(BH1750_SECONDARY_ADDRESS, BH1750_POWER_ON) ||
        !write_command_to(BH1750_SECONDARY_ADDRESS, BH1750_RESET) ||
        !write_command_to(BH1750_SECONDARY_ADDRESS, BH1750_CONT_HIRES)) {
        secondary_sensor_present = false;
        secondary_sample_valid = false;
        secondary_next_probe = now + LIGHT_RETRY_MS;
        return false;
    }
    secondary_sensor_present = true;
    secondary_next_sample = now + LIGHT_HIRES_INTERVAL_MS;
    return true;
}
static void lose_secondary_sensor(uint32_t now) {
    if (calibration.active && calibration.dual_sensor) calibration.invalid = true;
    secondary_sensor_present = false;
    secondary_sample_valid = false;
    secondary_next_probe = now + LIGHT_RETRY_MS;
}
static bool read_sample(uint32_t now) {
    uint8_t bytes[2];
    if (i2c_read_timeout_us(LIGHT_I2C, BH1750_PRIMARY_ADDRESS, bytes, sizeof(bytes),
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
static bool read_secondary_sample(uint32_t now) {
    uint8_t bytes[2];
    if (i2c_read_timeout_us(LIGHT_I2C, BH1750_SECONDARY_ADDRESS, bytes,
                            sizeof(bytes), false, LIGHT_I2C_TIMEOUT_US) != 2) {
        lose_secondary_sensor(now);
        return false;
    }
    uint32_t raw = ((uint32_t)bytes[0] << 8) | bytes[1];
    secondary_latest_tenths = (raw * 25u + 1u) / 3u;
    secondary_latest_at = now;
    secondary_sample_valid = true;
    secondary_next_sample = now + LIGHT_HIRES_INTERVAL_MS;
    return true;
}
static void calibration_sample(uint32_t value) {
    if (!calibration.active || calibration.dual_sensor) return;
    if (!calibration.samples || value < calibration.minimum)
        calibration.minimum = value;
    if (!calibration.samples || value > calibration.maximum)
        calibration.maximum = value;
    calibration.total += value;
    ++calibration.samples;
}
static bool pair_is_fresh(uint32_t now) {
    if (!sensor_present || !secondary_sensor_present ||
        !sample_valid || !secondary_sample_valid ||
        (uint32_t)(now - latest_at) > LIGHT_FRESH_MS ||
        (uint32_t)(now - secondary_latest_at) > LIGHT_FRESH_MS)
        return false;
    uint32_t primary_age = now - latest_at;
    uint32_t secondary_age = now - secondary_latest_at;
    uint32_t skew = primary_age > secondary_age
        ? primary_age - secondary_age : secondary_age - primary_age;
    return skew <= 250u;
}
static void calibration_sample_pair(uint32_t now) {
    if (!calibration.active || !calibration.dual_sensor) return;
    if (!pair_is_fresh(now)) return;
    if ((int32_t)(latest_at - calibration.started_at) < 0 ||
        (int32_t)(secondary_latest_at - calibration.started_at) < 0)
        return;
    if (calibration.pair_sampled &&
        (latest_at == calibration.last_primary_at ||
         secondary_latest_at == calibration.last_secondary_at))
        return;
    calibration.pair_sampled = true;
    calibration.last_primary_at = latest_at;
    calibration.last_secondary_at = secondary_latest_at;
    calibration.primary_total += latest_tenths;
    calibration.secondary_total += secondary_latest_tenths;
    calibration.mean_total += (uint64_t)latest_tenths + secondary_latest_tenths;
    calibration.difference_total +=
        (int64_t)secondary_latest_tenths - (int64_t)latest_tenths;
    ++calibration.samples;
}
static int32_t rounded_average_signed(int64_t total, uint32_t count) {
    uint64_t magnitude = total < 0
        ? (uint64_t)(-(total + 1)) + 1u : (uint64_t)total;
    int32_t rounded = (int32_t)((magnitude + count / 2u) / count);
    return total < 0 ? -rounded : rounded;
}
static void calibration_finish(uint32_t now) {
    calibration.active = false;
    calibration.pending = true;
    memset(&calibration.result, 0, sizeof(calibration.result));
    calibration.result.dual_sensor = calibration.dual_sensor;
    calibration.result.samples = calibration.samples;
    if (!calibration.samples ||
        (calibration.dual_sensor &&
         (calibration.invalid || !pair_is_fresh(now))))
        return;
    calibration.result.valid = true;
    if (calibration.dual_sensor) {
        calibration.result.average_primary_tenths =
            (uint32_t)((calibration.primary_total + calibration.samples / 2u) /
                       calibration.samples);
        calibration.result.average_secondary_tenths =
            (uint32_t)((calibration.secondary_total + calibration.samples / 2u) /
                       calibration.samples);
        calibration.result.mean_center_half_tenths =
            (uint32_t)((calibration.mean_total + calibration.samples / 2u) /
                       calibration.samples);
        calibration.result.difference_center_tenths =
            rounded_average_signed(calibration.difference_total,
                                   calibration.samples);
        calibration.result.average_tenths =
            (calibration.result.mean_center_half_tenths + 1u) / 2u;
        calibration.result.average_lux =
            (calibration.result.average_tenths + 5u) / 10u;
        return;
    }
    calibration.result.minimum_lux = (calibration.minimum + 5u) / 10u;
    calibration.result.maximum_lux = (calibration.maximum + 5u) / 10u;
    calibration.result.average_tenths = (uint32_t)((calibration.total + calibration.samples/2u) / calibration.samples);
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
    sensor_present = secondary_sensor_present = false;
    sample_valid = secondary_sample_valid = fault_pending = false;
    next_probe = secondary_next_probe = now;
    (void)probe(now);
    (void)probe_secondary(now);
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
    if (!secondary_sensor_present && reached(now, secondary_next_probe))
        (void)probe_secondary(now);
    bool new_sample = false;
    bool new_secondary_sample = false;
    if (sensor_present && reached(now, next_sample)) new_sample = read_sample(now);
    if (secondary_sensor_present && reached(now, secondary_next_sample))
        new_secondary_sample = read_secondary_sample(now);
    if (new_sample && !calibration.dual_sensor) calibration_sample(latest_tenths);
    if (new_sample || new_secondary_sample) calibration_sample_pair(now);
    if (calibration.active && reached(now, calibration.deadline))
        calibration_finish(now);
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
bool light_sensor_latest_pair(LightSensorSnapshot *snapshot, uint32_t now) {
    if (!snapshot) return false;
    memset(snapshot, 0, sizeof(*snapshot));
    snapshot->primary_valid = light_sensor_latest(
        &snapshot->primary_lux_tenths, &snapshot->primary_age_ms, now);
    snapshot->secondary_present = secondary_sensor_present;
    if (secondary_sensor_present && secondary_sample_valid &&
        (uint32_t)(now - secondary_latest_at) <= LIGHT_FRESH_MS) {
        snapshot->secondary_valid = true;
        snapshot->secondary_lux_tenths = secondary_latest_tenths;
        snapshot->secondary_age_ms = now - secondary_latest_at;
    }
    return snapshot->primary_valid;
}
bool light_sensor_calibration_start(uint32_t duration_ms, uint32_t now) {
    if (!sensor_present || calibration.active || watch_state.active ||
        duration_ms < 100u ||
        duration_ms > 60000u) return false;
    memset(&calibration, 0, sizeof(calibration));
    calibration.active = true;
    calibration.deadline = now + duration_ms;
    calibration.started_at = now;
    return true;
}
bool light_sensor_pair_calibration_start(uint32_t duration_ms, uint32_t now) {
    if (calibration.active || watch_state.active || duration_ms < 100u ||
        duration_ms > 60000u || !pair_is_fresh(now))
        return false;
    memset(&calibration, 0, sizeof(calibration));
    calibration.active = true;
    calibration.dual_sensor = true;
    calibration.started_at = now;
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
