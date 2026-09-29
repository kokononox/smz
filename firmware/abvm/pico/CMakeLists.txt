cmake_minimum_required(VERSION 3.13)

if(NOT DEFINED ENV{PICO_SDK_PATH})
    message(FATAL_ERROR "PICO_SDK_PATH must point to a Pico SDK checkout")
endif()
include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(ams_abvm_pico C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

pico_sdk_init()
find_package(Python3 REQUIRED COMPONENTS Interpreter)

set(ABVM_PROGRAM "" CACHE FILEPATH "Verified program.abp embedded in the UF2")
if(NOT ABVM_PROGRAM)
    message(FATAL_ERROR "Set -DABVM_PROGRAM=/absolute/path/program.abp")
endif()
get_filename_component(ABVM_PROGRAM "${ABVM_PROGRAM}" ABSOLUTE)
if(NOT EXISTS "${ABVM_PROGRAM}")
    message(FATAL_ERROR "ABVM_PROGRAM does not exist: ${ABVM_PROGRAM}")
endif()

set(ABVM_ROOT "${CMAKE_CURRENT_LIST_DIR}/..")
set(REPO_ROOT "${ABVM_ROOT}/../..")
set(GENERATED_PROGRAM "${CMAKE_CURRENT_BINARY_DIR}/abvm_program_image.c")

add_custom_command(
    OUTPUT "${GENERATED_PROGRAM}"
    COMMAND "${Python3_EXECUTABLE}" "${REPO_ROOT}/tools/abvm_embed.py"
            "${ABVM_PROGRAM}" "${GENERATED_PROGRAM}"
    DEPENDS "${ABVM_PROGRAM}" "${REPO_ROOT}/tools/abvm_embed.py"
    VERBATIM
)

add_executable(ams_abvm_pico
    main.c
    hid_keyboard.c
    usb_descriptors.c
    "${ABVM_ROOT}/src/abvm_vm.c"
    "${GENERATED_PROGRAM}"
)

target_include_directories(ams_abvm_pico PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}"
    "${ABVM_ROOT}/include"
)
target_compile_options(ams_abvm_pico PRIVATE
    -Wall -Wextra -Werror
)
target_compile_definitions(ams_abvm_pico PRIVATE
    CFG_TUSB_OS=OPT_OS_PICO
)
target_link_libraries(ams_abvm_pico
    pico_stdlib
    tinyusb_device
    tinyusb_board
)

pico_enable_stdio_usb(ams_abvm_pico 0)
pico_enable_stdio_uart(ams_abvm_pico 0)
pico_add_extra_outputs(ams_abvm_pico)
