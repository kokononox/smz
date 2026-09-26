# Classroom Studio ARM 2.8.4 — portable relative mouse

This is the exact ARM 2.7 source currently used on the Pro Micro, with one
focused change: `MMOVE|dx,dy,rel,2` now emits genuine relative HID reports.
Windows applies each delta to the cursor's real current position, so no bridge,
`CURSOR|x,y`, absolute reset, or centre jump is required.

ARM 2.8.4 uses one Arduino-core `PortableMouse` interface for movement, buttons and
wheel. Existing `MMOVE`, `HMOVE`, `HRANDOM`, sound, framed UART, encrypted USB,
HOSTUSB, HALT and button behavior remain available. ARM 2.8.4 omits the optional bridge-only `HSETCUR` ledger sync to stay within
the standard Caterina flash limit; the supported portable path never calls it.

Keep all source files, including `portable_relative_mouse.h`, in one Arduino sketch folder. Copy your existing private
`ams_key.h` beside them (never commit it); `ams_key.example.h` is only a template.
Open `ams_board28.ino`, select the same Classroom Studio Board / Pro Micro
5V 16 MHz profile used for ARM 2.7, compile, and upload. The verified build uses
28,594 of 28,672 bytes, so it fits the normal Caterina/Leonardo bootloader profile;
ISP remains optional.

## ARM 2.8.4 replay pacing

Dense relative `HANDPATH` playback keeps the exact ARM 2.8.1 DDA geometry and
sub-three-pixel HID reports, but removes the extra `delay(1)` between native
relative reports. `USB_Send` remains the pacing/back-pressure boundary, so the
firmware no longer pays both USB endpoint time and an additional software sleep.
Absolute, streamed legacy, click, wheel and sound paths are unchanged.

ARM 2.8.4 additionally chooses the minimum safe DDA report count whose integer
steps remain at or below three Euclidean pixels, and lets the bounded brain-link UART buffer provide back-pressure without an extra
blocking flush; `println` still blocks safely if that buffer fills.

## ARM 2.8.4 compact relative transport

The Pico may send `MR|dx,dy` after `HVER` advertises `MR=1`. The command keeps
the existing checksum frame, DDA geometry and HID ceiling while replacing the
long `MMOVE|dx,dy,rel,2` payload. The compact reply is `OK|MR`; legacy MMOVE
remains accepted for backward compatibility.
