#ifndef TEST_HARDWARE_GPIO_H
#define TEST_HARDWARE_GPIO_H
#ifndef PICO_UINT_DEFINED
#define PICO_UINT_DEFINED
typedef unsigned int uint;
#endif
#define GPIO_FUNC_UART 2u
#define GPIO_FUNC_I2C 3u
void gpio_set_function(uint pin, uint function);
void gpio_pull_up(uint pin);
#endif
