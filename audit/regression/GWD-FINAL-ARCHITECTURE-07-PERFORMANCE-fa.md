# A11 | اندازه‌گیری کارایی V268 در برابر V289-R

**NOT_RUN:** اعداد زیر عمداً خالی‌اند. روی Windows VM یکسان، SDK و DPI یکسان و snapshotهای جدا برای HKCU/AppData اجرا شود. مبنا V268 `5f709d4129304a5cb902205e0b3cf8f53db097a7` و نامزد V289-R با SHA تحویل. داده و IIS/SQL فقط ساختگی باشند.

| معیار | طرح اندازه‌گیری | V268 | V289-R |
|---|---|---|---|
| Startup cold/warm | ۱۰ سرد، ۲۰ گرم؛ تا paint و فوکوس اولین پنجره | — | — |
| ناوبری و UI stalls | ۳۰ مسیر یکسان؛ p50/p95/max از کلیک تا paint، نه CPU به‌جای مکث | — | — |
| CPU/RAM و GDI/handles/threads | PID، بازه نمونه یکسان، idle/active جدا؛ Working Set/Private bytes/threads | — | — |
| History | JSONL ساختگی ۱k/۱۰k/۱۰۰k؛ cold/warm/append؛ prefix هنوز به صورت خطی تأیید می‌شود | — | — |
| Log/Diagnostics | preview و export ZIP فایل ساختگی ۵/۵۰/۲۰۰ MB جدا؛ اصل غیرمتغیر، بسته کامل | — | — |
| Long session | ۲ ساعت تناوب idle/فعال و ۳۰ چرخه بازبینی؛ روند نشت پس از آرام‌شدن | — | — |

نسخه Windows/.NET، CPU، RAM، زمان، DPI، تعداد نمونه و شواهد پاک‌سازی‌شده ثبت شوند. هیچ درصد بهبود بدون دادهٔ واقعی گزارش نشود.
