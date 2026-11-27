---
type: feat
scope: infra
related_files:
  - src/Ziwa.Domain/Bridge/PhoneBridgeModels.cs:1
  - src/Ziwa.Rendering/Qr/QrCodeBitmapRenderer.cs:1
  - src/Ziwa.Application/Bridge/PhoneBridgeCanvasInserter.cs:1
  - src/Ziwa.Infrastructure/Bridge/KestrelPhoneBridgeService.cs:1
  - src/Ziwa.App/Views/PhoneBridgeView.axaml:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:39
---
تنفيذ جسر الهاتف الفوري (P5-02) عبر خادم Kestrel المحلي (بصفحة `/cam` وفحص التوكن `401` وسقف `413` والتوقيع السحري `415`) ومزامنة Supabase Realtime/Storage عبر نطاق `grido.cloud-ip.cc/cam` مع مولّد QR نقي وإدراج تلقائي في الوضع الحر وخانات الكولاج.
