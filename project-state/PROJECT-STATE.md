# Classroom Studio — وضعیت زنده‌ی پروژه

> منبع حقیقت جاری پروژه؛ تاریخچه‌ی کامل در صفحه‌ی Notion پروژه نگهداری می‌شود.

## وضعیت لحظه‌ای — ۲۰۲۶-۱۰-۰۵

- مخزن فعال: `kokononox/smz`.
- شاخهٔ ادغام Native جاری: `arch/abvm-phase0`.
- PRهای `#123`، `#124` و `#125` به‌ترتیب زنجیره DC/Login، سیاست‌های پایان گروه موازی و `timeBudget` را اضافه کرده‌اند.
- مستند handoff قبلی از PR `#1` روی commit `2fd4f3c` در `arch/abvm-phase0` ادغام شد.
- نسخهٔ محلی `0.9.68` آماده است: باگ Native `infinite` رفع شده، موتور موس چهار حالت آهسته‌تغییر، مسیر دومرحله‌ای برای حرکت‌های بلند و تنوع board-local در هر بوت دارد.
- قرارداد فعلی ماهیگیری: بودجه ۹ دقیقه، سقف ۸۰ دور، مسیر مستقل و واقعاً infinite موس، Catch با threshold 40 و Soft Boundary چهاردرصد.
- فایل AMSJ دارای پروفایل شخصی حرکت دست است و بدون اجازهٔ جداگانه در مخزن عمومی ثبت نمی‌شود؛ جزئیات و SHA-256 در `docs/session-2026-10-05-timebudget-human-mouse.md` ثبت شده‌اند.
- معماری پرتابل حفظ شده است: Pico مغز مستقل و Pro Micro بازوی Mouse/Sound می‌مانند؛ هیچ وابستگی Host، شبکه، Vision یا OS اضافه نشده است.
- گیت محلی نسخهٔ جدید: ۶۷ تست Portable/ABVM، هجده تست Bridge، C smoke، بیلد Pico، سی قالب هویت، اپ Windows و کامپایل هر دو پروژهٔ تست موفق.

## معماری تثبیت‌شده

- Classroom Studio محیط ساخت، تنظیم، کالیبراسیون، تست و Export است.
- Pico مغز اجرایی مستقل، مجری Keyboard، سنسور BH1750 و موتور پرتابل است.
- Pro Micro بازوی Mouse و Sound Sensor است و از طریق UART با Pico کار می‌کند.
- BH1750 روی Pico با `SDA=GP20`، `SCL=GP21` و آدرس `0x23` است.
- قراردادهای `LUX?`، `WLUX`، `TRGLUX` و `LCAL` حفظ شده‌اند.

## نتیجه‌ی فازهای Light Telemetry

1. پروتکل فقط‌خواندنی `LUX?` و تست Firmware — کامل و merge شده.
2. Bridge typed و Watch Service بدون overlap — کامل و merge شده.
3. تب مستقل Status با Lux زنده، health، نمودار و آمار — کامل و merge شده.
4. Light State Profiles، classifier، persistence و calibration — کامل، کالیبره و آمادهٔ merge.

## Bridge correlation

Firmware برای درخواست `LUX?` پاسخ `OK|LUX|...` یا خطای `ERR|...|LUX` می‌دهد. بستهٔ اجرایی از entry point سازگار `light_state_bridge.py` استفاده می‌کند که alias پاسخ، جداسازی `EVT` و رد پاسخ stale را ایزوله می‌کند. Bridge قدیمی بدون refactor گسترده حفظ شده تا ریسک regression مسیرهای تثبیت‌شده کم بماند. این مسیر روی Windows و سخت‌افزار واقعی تأیید شده است.

## نتیجه‌ی QA سخت‌افزاری فاز ۴

- اتصال خودکار Pico روی `COM5` با Firmware `pico-light 0.9.64h` موفق بود.
- دریافت پیوستهٔ `OK|LUX` و عبور `EVT|HOSTUSB|UP` بدون mispair تأیید شد.
- نمودار، آمار، Stop، stale state، RTL و عرض کم تأیید شدند.
- Apply، Reset، Save و persistence پس از restart تأیید شدند.
- ورودی منفی، `NaN` و فیلد خالی رد شدند.
- تشخیص و گذار هر شش وضعیت بدون Ambiguous پایدار تأیید شد.
- برای این فاز نیازی به تغییر Firmware نیست.

## پروفایل‌های کالیبره‌شده

هر شش پروفایل فعال‌اند؛ `StableDuration = 1250 ms` و `Hysteresis = 1 Lux`:

- Desktop: `35.8 ±1`، بازهٔ `34.8..36.8`.
- Login یا DC: `5.8 ±2.6`، بازهٔ `3.2..8.4`.
- Character dashboard: `15.8 ±1`، بازهٔ `14.8..16.8`.
- Entering-game loading: `38.3 ±0.5`، بازهٔ `37.8..38.8`.
- Game: `26.7 ±1`، بازهٔ `25.7..27.7`.
- Targeted: `30 ±1`، بازهٔ `29..31`.

بازه‌های مؤثر overlap ندارند.

## قرارداد Login و DC

- Login و DC یک پروفایل مشترک دارند؛ Lux علت را تشخیص نمی‌دهد.
- Login مستقیماً جریان مشترک ورود را اجرا می‌کند.
- Disconnect دقیقاً یک `ESC` و سپس همان جریان مشترک Login را اجرا می‌کند.
- classifier این قرارداد را اجرا نمی‌کند؛ فقط وضعیت را گزارش می‌دهد.

## اصلاح CI

دو workflow مستقل از الگوی یکسان `light-state-test-${github.run_number}` استفاده می‌کردند. مستقل‌بودن run number موجب collision تگ و check قرمز کاذب می‌شد. workflow تکراری حذف شد و AutoCycle تنها بسته‌ساز gated باقی ماند. پس از اصلاح، همهٔ gateها سبز شدند.

## مرز ایمنی

- Live Watch و classifier فقط مشاهده‌ای و بدون Side Effect هستند.
- هیچ Macro، فشردن کلید، BEEP، `WLUX`، `TRGLUX`، Launch یا Auto Resume از مسیر تشخیص اجرا نمی‌شود.
- گیت اجرایی فاز ۵ باید جداگانه طراحی، تست و تأیید شود.

## گام بعدی

1. ادغام PR نسخهٔ `0.9.68` پس از سبز ماندن گیت‌های محلی.
2. فلش و تست سخت‌افزاری `runtime-NB01` با AMSJ خصوصی v8.
3. تنظیم فقط از محدوده‌های فعلی پس از مشاهدهٔ رفتار واقعی؛ معماری hostless تغییر نکند.

## گیت‌های دائمی

1. build اپ بدون خطا.
2. TestRunner با `0 failed`.
3. parity خروجی عادی و AutoCycle.
4. sensitive guard سبز.
5. merge فقط از مسیر PR.
6. تغییر UI فقط بعد از آماده‌شدن Protocol و Bridge typed.
