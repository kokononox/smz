#ifndef AMS_ABVM_BUZZER_H
#define AMS_ABVM_BUZZER_H

#include <stdbool.h>
#include <stdint.h>

typedef enum BuzzerCue {
    BUZZER_CUE_START,
    BUZZER_CUE_STOP,
    BUZZER_CUE_PAUSE,
    BUZZER_CUE_RESUME,
    BUZZER_CUE_TIMEOUT,
    BUZZER_CUE_ERROR,
    BUZZER_CUE_WHISPER,
    BUZZER_CUE_WHISPER_REPEAT,
    BUZZER_CUE_CALIBRATION_OK,
} BuzzerCue;

typedef struct BuzzerTone { uint16_t hz, duration_ms, gap_ms; } BuzzerTone;

void buzzer_init(void);
void buzzer_service(uint32_t now);
void buzzer_play(BuzzerCue cue, uint32_t now);
void buzzer_play_tone(uint16_t hz, uint16_t duration_ms, uint32_t now);
void buzzer_play_tone_ex(uint16_t hz, uint16_t duration_ms, uint8_t volume,
                         uint8_t envelope, uint32_t now);
void buzzer_play_sequence(const BuzzerTone *tones, uint8_t count, uint8_t volume,
                          uint8_t envelope, uint32_t now);
void buzzer_set_calibration_style(uint8_t volume, uint8_t envelope);
void buzzer_guard_transition(uint8_t profile_id, uint32_t now);
void buzzer_set_calibration_custom(const BuzzerTone *tones, uint8_t count,
                                   uint8_t volume, uint8_t envelope);
void buzzer_set_system_cue(uint8_t cue_id, const BuzzerTone *tones, uint8_t count,
                           uint8_t volume, uint8_t envelope);
void buzzer_calibration_enter(uint8_t selection, bool sound, uint32_t now);
void buzzer_calibration_position(uint8_t selection, bool sound, uint32_t now);
void buzzer_calibration_record_start(bool sound, uint32_t now);
void buzzer_calibration_sound_target(uint32_t now);
void buzzer_calibration_stage_complete(uint8_t stage, uint32_t now);
void buzzer_calibration_save_success(uint32_t now);
void buzzer_calibration_save_error(uint32_t now);
void buzzer_calibration_complete(uint32_t now);
void buzzer_calibration_exit(uint32_t now);
void buzzer_watchdog_alarm_start(uint32_t now);
void buzzer_watchdog_alarm_stop(void);
bool buzzer_watchdog_alarm_active(void);
void buzzer_silence(void);
bool buzzer_active(void);

#endif
