# smc — Classroom Studio CI

## Start here

- [Current hardware changelog](CHANGELOG-CURRENT.md) — مشکلات هر Build، علت، اصلاح، نتیجهٔ CI و نتیجهٔ تست واقعی
- [Latest Classroom Studio test releases](https://github.com/noonoix/smz/releases)
- Active hardware branch: `fix/portable-relative-mouse`

## Current build contract

هر Push مؤثر بر Classroom Studio یا Runtime مدرن در شاخهٔ فعال:

1. Changelog را بررسی می‌کند؛ تغییر Build بدون به‌روزرسانی `CHANGELOG-CURRENT.md` رد می‌شود.
2. Python runtime و Bridge را Compile-check می‌کند.
3. Classroom Studio و TestRunner را روی Windows می‌سازد.
4. Golden-100 و قراردادهای مدرن را اجرا می‌کند؛ با هر تست قرمز، ZIP منتشر نمی‌شود.
5. در حالت موفق `Classroom-Studio-current.zip`، `sha256.txt` و `ci-test-output.txt` را منتشر می‌کند.
6. متن Release را از جدیدترین بخش `CHANGELOG-CURRENT.md` می‌سازد.

> CI سبز به معنی آماده‌بودن Candidate است؛ تأیید نهایی Hardware فقط پس از ثبت لاگ واقعی در Changelog انجام می‌شود.
