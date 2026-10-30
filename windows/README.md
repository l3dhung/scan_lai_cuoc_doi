# Life-scanner cho Windows

Một file `Life-scanner.exe` duy nhất, chạy trên Windows 10/11 64-bit mà không cần cài .NET, không cần mạng.

- Giao diện là đúng `dist/index.html` của bản chính, chạy trong Microsoft Edge WebView2 (có sẵn trên Windows 11 và hầu hết máy Windows 10 đã cập nhật; máy nào thiếu, app sẽ báo và mở trang tải của Microsoft).
- Phần lưu file giống bản Mac (`macos/serve.py`): chọn thư mục "Lưu vào", tự tạo thư mục con theo loại thủ tục, tự đánh số `<thủ tục>_1.pdf`, `_2.pdf`…, không bao giờ ghi đè, "Mở trong Explorer".
- Dữ liệu: danh sách thư mục đã chọn ở `%APPDATA%\Life-scanner\bridge.json`, hồ sơ trình duyệt (cài đặt trong app) ở `%LOCALAPPDATA%\Life-scanner\WebView2`. File .exe để ở đâu cũng được, kể cả USB.

## Build

- Trên Mac/Linux có .NET 8 SDK: `windows/build-exe.sh` → `windows/out/Life-scanner.exe`.
- Trên Windows: `dotnet publish windows\LifeScanner.csproj -c Release -o windows\out`.
- Trên GitHub: tab Actions → "Build Windows exe" → Run workflow, rồi tải file trong mục Artifacts.

Build lại `dist/` trước (`node scripts/build-standalone.cjs`) nếu vừa sửa giao diện.

File .exe chưa được ký số, nên lần đầu mở Windows SmartScreen có thể cảnh báo: bấm "More info" → "Run anyway".
