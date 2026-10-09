# Combined Pico firmware USB and filesystem configuration.
# HID remains enabled for Portable Steps; CDC data is the Classroom Studio channel.
import storage
import usb_cdc
import usb_hid

# CIRCUITPY stays writable so physical calibration can persist its JSON files.
# The runtime still validates the complete bundle and keeps Guard fail-closed;
# writable storage is only a filesystem policy and does not enable Guard/routes.
storage.remount("/", readonly=False, disable_concurrent_write_protection=True)

usb_cdc.enable(console=True, data=True)
# usb_hid is intentionally not disabled: Pico HID is the keyboard path.
