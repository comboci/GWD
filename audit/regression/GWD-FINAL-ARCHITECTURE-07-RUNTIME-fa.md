# A12 | پذیرش ویندوز GWD V289-R

**همهٔ موارد NOT_RUN هستند.** فقط با تصمیم مالک روی VM دورریختنی، کاربر Windows مجزا و IIS/SQL و فایل‌های آزمایشی اجرا شوند. `GWDM_APPDATA_ROOT` به‌تنهایی HKCU را ایزوله نمی‌کند. نه Deploy شرکت، نه انتشار EXE، نه Merge.

ابتدا SHA، Windows، .NET 10 و Node را ثبت کنید. اسکریپت تاریخی V269 قفل ساختار private و SHAهای مبنای V268 است؛ آن را فقط روی checkout تاریخی V269-R (0121217a13057d7c832a9a9ddd7aa9acf5a099cc) به‌عنوان کنترل همان نسخه استفاده کنید. هیچ تست تاریخی تغییر نکرده. دستورهای زیر فقط **برای مالک** هستند:

```powershell
git rev-parse HEAD
node tests/verify-gwd-final-architecture-07.cjs
dotnet --info
dotnet build DeploymentManager.sln -c Release
$env:GWDM_APPDATA_ROOT = Join-Path $env:TEMP 'Gwd-Architecture07-V289'
$smoke = 'src/DeploymentManager.SmokeTests/DeploymentManager.SmokeTests.csproj'
foreach ($name in @('--ui-performance-phase-02','--ui-performance-phase-03','--ui-performance-phase-04',
 '--ui-performance-phase-05','--ui-performance-phase-06','--diagnostic-history','--sql-strategy','--workspace-sync')) {
  dotnet run --project $smoke -c Release --no-build -- $name
  if ($LASTEXITCODE) { throw "Smoke failed: $name" }
}
dotnet run --project $smoke -c Release --no-build
```

پروژه‌های `tests/IisSafety.Tests`، `tests/ReleaseVersionRegression`، `tests/CodeQuality.Tests` و `tests/CodeQuality.WindowsTests` را طبق runner تاریخی همان checkout فقط روی VM اجرا کنید. خطای Compile یا رفتار را ثبت کنید و false pass ندهید؛ گیت تاریخی شکست تأییدنشدهٔ پروژه جدید نیست.

| ID | سناریوی آزمایشی و مشاهده لازم |
|---|---|
| G01 | ۸ صفحه، Back/Next/Start Over، بازانتخاب، Profile و Log مشترک بدون از دست رفتن. |
| G02 | URL/پوشه محلی و شبکهٔ آزمایشگاهی کند، تغییر همان مسیر، پوشه غیرمعمول و Prepared Publish، نتیجه منقضی نشود و بررسی نهایی برقرار. |
| G03 | Load/Save پروفایل و تغییر SQL identity؛ secret ذخیره یا استفاده مجدد نشود. |
| G04 | Settings: دو پنجره، تغییر سریع، خروج، JSON معیوب؛ داده موجود خراب overwrite نشود، خطای ثبت دیده شود. فقط رمز ساختگی برای Remember/opt-out؛ صف قدیمی رمز را بازنگرداند. |
| G05 | IIS/SQL مستقل آزمایشگاه: Inventory و Cancel؛ دادهٔ خالیِ هنگام خطا به‌عنوان نتیجه سالم گزارش نشود. |
| G06 | دامنه Health پس از تغییر port/target/SQL؛ stale callback رد شود، بررسی صریح/دوره‌ای مشترک باشد و SQL صرف تایپ اجرا نشود. HTTP loopback اثبات deployment یا rollback نیست. |
| G07 | Deployment/Cancel/Backup/Rollback فقط سرویس ساختگی VM؛ marker و backup حفظ و شواهد مقصد/IIS/API/frontend ثبت شوند. تست روی داده یا سایت واقعی انجام ندهید. |
| G08 | History JSONL ساختگی: append، replace، truncate، malformed، BOM، sidecar خرابی/حذف، page و export کامل. فایل اصلی ثابت بماند. |
| G09 | Log/Diagnostics بزرگ و Refresh پیاپی: فایل اصلی سالم، ZIP کامل، و redaction طبق داده ساختگی. |
| G10 | Dashboard دو پروژه/هدف، Fast Local Test، نسخه ناموجود و سلامت ناقص؛ recorded metadata جدا از live، Cancel در حالت پنجره غیرمودال در دسترس. |
| G11 | Critical و Success/duplicates/priority/History/Diagnostics/countdown؛ اعلان پایین‌تر روی Critical ننشیند و dismissal marker را تغییر ندهد. |
| G12 | ۱۰۰/۱۲۵/۱۵۰/۱۷۵/۲۰۰٪ و 1366×768، 1920×1080، 2560×1440، 3840×2160؛ دو مانیتور، High Contrast، focus، Cancel و Recovery، Scroll/Splitter. |
| G13 | ۳۰ رفت‌وبرگشت و بازوبستن نوبتی فرم‌ها و ۲ ساعت نشست؛ GDI/CPU/RAM/handles/threads و رفتار Dispose ثبت شوند. |

برای هر ردیف، PASS/FAIL/NOT_RUN، SHA، timestamp، داده ساختگی و شواهد نتیجه را ثبت کنید. تا بررسی واقعی، نامزد را Release یا آماده Merge اعلام نکنید.
