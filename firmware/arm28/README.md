# Classroom Studio ARM 2.8.3 — authored-tick relative mouse

This is the exact ARM 2.7 source currently used on the Pro Micro, with one
focused change: `MMOVE|dx,dy,rel,2` now emits genuine relative HID reports.
Windows applies each delta to the cursor's real current position, so no bridge,
`CURSOR|x,y`, absolute reset, or centre jump is required.

ARM 2.8 uses one Arduino-core `PortableMouse` interface for movement, buttons and
wheel. Existing `MMOVE`, `HMOVE`, `HRANDOM`, sound, framed UART, encrypted USB,
HOSTUSB, HALT and button behavior remain available. `HSETCUR` still aligns the
virtual ledger when an optional host-assisted workflow uses it; the fully
portable path does not call it.

Keep all source files, including `portable_relative_mouse.h`, in one Arduino sketch folder. Copy your existing private
`ams_key.h` beside them (never commit it); `ams_key.example.h` is only a template.
Open `ams_board28.ino`, select the same Classroom Studio Board / Pro Micro
5V 16 MHz profile used for ARM 2.7, compile, and upload. The verified build uses
28,594 of 28,672 bytes, so it fits the normal Caterina/Leonardo bootloader profile;
ISP remains optional.

ARM 2.8.3 preserves each bounded relative `MMOVE` authored by the Pico as one
USB HID report. Earlier 2.8.2 firmware subdivided every point into 1–2 px
micro-reports at 1 ms intervals, which erased the v3.3 velocity jitter and made
reports arrive in bursts. Oversized legacy deltas are still split defensively
at the signed HID limit.
