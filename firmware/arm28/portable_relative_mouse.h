#pragma once
// Minimal single-report relative mouse for Arduino AVR/Leonardo.
// This is self-contained and does not require HID-Project's optional BootMouse.
#include <Arduino.h>
#include <HID.h>

#ifndef MOUSE_LEFT
#define MOUSE_LEFT   (1u << 0)
#define MOUSE_RIGHT  (1u << 1)
#define MOUSE_MIDDLE (1u << 2)
#endif

static const uint8_t _portableMouseDescriptor[] PROGMEM = {
  0x05, 0x01, 0x09, 0x02, 0xA1, 0x01,
  0x09, 0x01, 0xA1, 0x00,
  0x05, 0x09, 0x19, 0x01, 0x29, 0x08,
  0x15, 0x00, 0x25, 0x01, 0x95, 0x08, 0x75, 0x01, 0x81, 0x02,
  0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38,
  0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06,
  0xC0, 0xC0
};

struct PortableMouseReport {
  uint8_t buttons;
  int8_t x;
  int8_t y;
  int8_t wheel;
} __attribute__((packed));

class PortableMouse_ : public PluggableUSBModule {
public:
  PortableMouse_() : PluggableUSBModule(1, 1, epType_), buttons_(0), protocol_(HID_REPORT_PROTOCOL), idle_(1) {
    epType_[0] = EP_TYPE_INTERRUPT_IN;
    PluggableUSB().plug(this);
  }
  void begin() { releaseAll(); }
  void move(int8_t x, int8_t y, int8_t wheel = 0) {
    PortableMouseReport report = {buttons_, x, y, wheel};
    USB_Send(pluggedEndpoint | TRANSFER_RELEASE, &report, sizeof(report));
  }
  void press(uint8_t button) { buttons_ |= button; move(0, 0, 0); }
  void release(uint8_t button) { buttons_ &= (uint8_t)~button; move(0, 0, 0); }
  void releaseAll() { buttons_ = 0; move(0, 0, 0); }
protected:
  int getInterface(uint8_t* count) override {
    *count += 1;
    HIDDescriptor interface = {
      D_INTERFACE(pluggedInterface, 1, USB_DEVICE_CLASS_HUMAN_INTERFACE, HID_SUBCLASS_BOOT_INTERFACE, HID_PROTOCOL_MOUSE),
      D_HIDREPORT(sizeof(_portableMouseDescriptor)),
      D_ENDPOINT(USB_ENDPOINT_IN(pluggedEndpoint), USB_ENDPOINT_TYPE_INTERRUPT, USB_EP_SIZE, 0x01)
    };
    return USB_SendControl(0, &interface, sizeof(interface));
  }
  int getDescriptor(USBSetup& setup) override {
    if (setup.wIndex != pluggedInterface || setup.bmRequestType != REQUEST_DEVICETOHOST_STANDARD_INTERFACE) return 0;
    if (setup.wValueH == HID_HID_DESCRIPTOR_TYPE) {
      HIDDescDescriptor descriptor = D_HIDREPORT(sizeof(_portableMouseDescriptor));
      return USB_SendControl(0, &descriptor, sizeof(descriptor));
    }
    if (setup.wValueH == HID_REPORT_DESCRIPTOR_TYPE) {
      protocol_ = HID_REPORT_PROTOCOL;
      return USB_SendControl(TRANSFER_PGM, _portableMouseDescriptor, sizeof(_portableMouseDescriptor));
    }
    return 0;
  }
  bool setup(USBSetup& setup) override {
    if (setup.wIndex != pluggedInterface) return false;
    if (setup.bmRequestType == REQUEST_HOSTTODEVICE_CLASS_INTERFACE) {
      if (setup.bRequest == HID_SET_PROTOCOL) { protocol_ = setup.wValueL; return true; }
      if (setup.bRequest == HID_SET_IDLE) { idle_ = setup.wValueH; return true; }
    }
    if (setup.bmRequestType == REQUEST_DEVICETOHOST_CLASS_INTERFACE) {
      if (setup.bRequest == HID_GET_REPORT || setup.bRequest == HID_GET_PROTOCOL || setup.bRequest == HID_GET_IDLE) return true;
    }
    return false;
  }
private:
  uint8_t epType_[1];
  uint8_t buttons_;
  uint8_t protocol_;
  uint8_t idle_;
};

static PortableMouse_ PortableMouse;
