# Combined Pico firmware USB and filesystem configuration.
# HID remains enabled for Portable Steps; CDC data is the Classroom Studio channel.
import storage
import usb_cdc
import usb_hid

# The host owns CIRCUITPY. Physical calibration is persisted in checksummed
# microcontroller NVM, never by competing with Windows on the FAT volume.
storage.remount("/", readonly=True)

usb_cdc.enable(console=True, data=True)
# usb_hid is intentionally not disabled: Pico HID is the keyboard path.
