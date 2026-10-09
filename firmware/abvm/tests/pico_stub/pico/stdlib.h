#ifndef ABVM_TEST_PICO_STDLIB_H
#define ABVM_TEST_PICO_STDLIB_H

#include <stdbool.h>
#include <stdint.h>

typedef unsigned int uint;
typedef uint64_t absolute_time_t;

#define GPIO_IN false
#define PICO_ERROR_TIMEOUT (-1)

static inline absolute_time_t get_absolute_time(void) { return 0; }
static inline uint32_t to_ms_since_boot(absolute_time_t value) {
    return (uint32_t)value;
}
static inline void stdio_init_all(void) {}
static inline void gpio_init(uint pin) { (void)pin; }
static inline void gpio_set_dir(uint pin, bool out) {
    (void)pin; (void)out;
}
static inline void gpio_pull_up(uint pin) { (void)pin; }
static inline bool gpio_get(uint pin) { (void)pin; return true; }
static inline void sleep_ms(uint32_t value) { (void)value; }
static inline int getchar_timeout_us(uint32_t value) {
    (void)value; return PICO_ERROR_TIMEOUT;
}

#endif