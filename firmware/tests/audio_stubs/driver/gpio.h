#pragma once
using gpio_num_t = int;
constexpr int GPIO_NUM_4=4, GPIO_NUM_5=5, GPIO_NUM_6=6, GPIO_NUM_7=7, GPIO_NUM_16=16;
constexpr int GPIO_MODE_OUTPUT=1;
int gpio_set_direction(int, int);
int gpio_set_level(int, int);
