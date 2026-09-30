#ifndef AMS_ABVM_BUZZER_H
#define AMS_ABVM_BUZZER_H

#include <stdbool.h>
#include <stdint.h>

typedef enum BuzzerCue {
    BUZZER_CUE_START,
    BUZZER_CUE_STOP,
    BUZZER_CUE_PAUSE,
    BUZZER_CUE_RESUME,
    BUZZER_CUE_CATCH,
    BUZZER_CUE_TIMEOUT,
    BUZZER_CUE_ERROR,
    BUZZER_CUE_CALIBRATION_OK,
} BuzzerCue;

void buzzer_init(void);
void buzzer_service(uint32_t now);
void buzzer_play(BuzzerCue cue, uint32_t now);
void buzzer_play_stage(uint8_t stage, uint32_t now);
/* One continuous, envelope-shaped glide. Equal endpoints produce a held tone. */
void buzzer_play_smooth(uint16_t start_hz, uint16_t end_hz,
                        uint16_t duration_ms, uint32_t now);
void buzzer_silence(void);
bool buzzer_active(void);

#endif
