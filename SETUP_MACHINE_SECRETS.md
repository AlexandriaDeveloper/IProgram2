# 🔐 دليل إعداد أسرار الاتصال (User Secrets) على جهاز جديد

هذا الدليل يشرح خطوة بخطوة كيفية نقل وإعداد ملف الـ **User Secrets** الخاص بمشروع **IProgram** على جهاز كمبيوتر جديد، لتشغيل الاتصال بقواعد بيانات Azure (`IProgramDb2026` و `IProgramDb2027`) دون الحاجة لكتابة كلمات المرور داخل ملفات المشروع المشتركة (`appsettings.json`).

---

## 📌 معلومات تعريف المشروع (UserSecretsId)
المشروع مسجل برقم تعريف ثابت داخل ملف [`src/Api/Auth.Api.csproj`](file:///f:/Prog-Projects/IProgram/src/Api/Auth.Api.csproj):
```xml
<UserSecretsId>534c15a8-262c-4c77-848e-9abbba0a57f1</UserSecretsId>
```

مسار ملف الـ `secrets.json` على أنظمة التشغيل:
* **Windows:**
  `%APPDATA%\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1\secrets.json`
  *(المسار الفعلي الكامل: `C:\Users\<اسم_المستخدم>\AppData\Roaming\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1\secrets.json`)*
* **macOS / Linux:**
  `~/.microsoft/usersecrets/534c15a8-262c-4c77-848e-9abbba0a57f1/secrets.json`

---

## 🚀 الطريقة الأولى: النسخ المباشر من جهازك الحالي (الأسرع والأسهل)

إذا كان بإمكانك نقل الملف عن طريق فلاشة (USB) أو إرساله لنفسك بشكل آمن:

### 1. على جهازك الحالي (القديم):
افتح **PowerShell** ونفذ الأمر التالي لنسخ ملف الـ Secrets إلى سطح المكتب (يعمل سواء كان سطح المكتب مرتبطاً بـ OneDrive أو عادياً):
```powershell
# الحصول على مسار سطح المكتب الصحيح ونسخ الملف إليه
$desktop = [Environment]::GetFolderPath('Desktop')
Copy-Item "$env:APPDATA\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1\secrets.json" -Destination "$desktop\secrets.json"
```

### 2. على الجهاز الجديد:
بعد وضع ملف `secrets.json` على سطح المكتب على الجهاز الجديد، افتح **PowerShell** وشغّل:
```powershell
# مسار سطح المكتب
$desktop = [Environment]::GetFolderPath('Desktop')

# إنشاء المجلد الخاص بالمشروع على الجهاز الجديد
$targetFolder = "$env:APPDATA\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1"
if (-not (Test-Path $targetFolder)) {
    New-Item -ItemType Directory -Path $targetFolder -Force
}

# نسخ الملف إلى مسار UserSecrets
Copy-Item "$desktop\secrets.json" -Destination "$targetFolder\secrets.json" -Force

Write-Output "تم تثبيت ملف الـ Secrets بنجاح على الجهاز الجديد!"
```

---

## 🛠️ الطريقة الثانية: عبر سطر الأوامر (dotnet user-secrets)

إذا قمت بعمل Clone للمشروع على الجهاز الجديد وتريد إدخال البيانات عبر التيرمنال:

1. افتح التيرمنال وادخل إلى مجلد `src/Api`:
   ```bash
   cd src/Api
   ```

2. نفذ الأوامر التالية لإضافة مفاتيح الاتصال:
   ```powershell
   # 1. اتصال قاعدة 2026
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramDb2026;Persist Security Info=False;User ID=<USER_NAME>;Password=<PASSWORD>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

   # 2. اتصال قاعدة 2027
   dotnet user-secrets set "ConnectionStrings:CON2027" "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramDb2027;Persist Security Info=False;User ID=<USER_NAME>;Password=<PASSWORD>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

   # 3. مفتاح توثيق JWT
   dotnet user-secrets set "Token:Key" "<YOUR_JWT_KEY>"
   ```

---

## 💻 الطريقة الثالثة: من داخل Visual Studio أو VS Code

### من داخل Visual Studio:
1. افتح الـ Solution `IProgram.sln`.
2. في نافذة **Solution Explorer**, اضغط كليك يمين على مشروع **Auth.Api** (أو `src/Api`).
3. اختر **Manage User Secrets** (إدارة أسرار المستخدم).
4. سيفتح لك ملف `secrets.json` فارغاً تلقائياً.
5. ضع بداخله الهيكل التالي واملأ البيانات:
   ```json
   {
     "ConnectionStrings:DefaultConnection": "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramDb2026;Persist Security Info=False;User ID=<USER_NAME>;Password=<PASSWORD>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;",
     "ConnectionStrings:CON2027": "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramDb2027;Persist Security Info=False;User ID=<USER_NAME>;Password=<PASSWORD>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;",
     "Token:Key": "<YOUR_JWT_KEY>"
   }
   ```
6. احفظ الملف (`Ctrl + S`).

---

## ✅ التحقق من نجاح التثبيت على الجهاز الجديد

للتأكد من أن الجهاز الجديد يقرأ الـ Secrets بشكل صحيح:

1. ادخل إلى مجلد `src/Api`:
   ```bash
   cd src/Api
   dotnet user-secrets list
   ```
   يجب أن يظهر لك:
   * `ConnectionStrings:CON2027`
   * `ConnectionStrings:DefaultConnection`
   * `Token:Key`

2. شغّل التطبيق:
   ```bash
   dotnet run --project src/Api/Auth.Api.csproj
   ```
   وافتح المتصفح على:
   * `http://localhost:5000/health` $\rightarrow$ يجب أن يرجع `Healthy`.
   * `http://localhost:5000/api/account/databases` $\rightarrow$ يجب أن يرجع قاعدتي `2026` و `2027`.

---

## ⚠️ تنبيه هام جداً: جدار حماية خادم Azure (SQL Firewall Rules)
إذا كان الجهاز الجديد يعمل على شبكة إنترنت مختلفة (راوتر / IP خارجي جديد):
* قد تظهر رسالة خطأ عند أول محاولة اتصال تفيد بأن عنوان الـ IP غير مصرح له بالدخول:
  `Client with IP address 'xxx.xxx.xxx.xxx' is not allowed to access the server.`
* **الحل:**
  1. ادخل إلى **Azure Portal**.
  2. افتح خادم الـ SQL Server: `iprogram-sql-prod-01`.
  3. من القائمة الجانبية ادخل على **Security** $\rightarrow$ **Networking**.
  4. اضغط على **Add your client IPv4 address** ثم اضغط **Save**.
