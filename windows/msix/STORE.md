# Đưa Life-scanner lên Microsoft Store

## 1. Tài khoản và tên app (làm một lần)

1. Đăng ký tài khoản nhà phát triển cá nhân tại https://developer.microsoft.com/microsoft-store/register và làm theo bước xác minh danh tính.
2. Vào Partner Center → Apps and games → **New product** → **MSIX or PWA app** → giữ tên `Life-scanner`.
3. Mở **Product management → Product identity**, chép 3 dòng sau vào `windows/msix/AppxManifest.xml`:
   - `Package/Identity/Name` → thuộc tính `Name` của `<Identity>`
   - `Package/Identity/Publisher` → thuộc tính `Publisher` của `<Identity>` (dạng `CN=XXXXXXXX-XXXX-...`)
   - `Package/Properties/PublisherDisplayName` → `<PublisherDisplayName>`
4. Commit và push, GitHub tự build lại.

## 2. Lấy gói

GitHub → Actions → **Build Windows MSIX** → lần chạy mới nhất → Artifacts:

- `Life-scanner-msix-store`: file `.msix` chưa ký, dùng để nộp lên Store (Store tự ký).
- `Life-scanner-msix-test`: bản đã ký tạm để cài thử trên máy Windows, xem `TEST-INSTALL.txt` bên trong.

Mỗi lần nộp bản mới, tăng `version` trong `app.config.json` (Store không nhận lại số phiên bản cũ).

## 3. Tạo bản nộp (Submission)

| Mục | Điền |
| --- | --- |
| Pricing and availability | Free, các thị trường muốn bán |
| Properties → Category | Productivity (hoặc Business) |
| Properties → Privacy policy URL | https://github.com/l3dhung/scan_lai_cuoc_doi/blob/main/PRIVACY.md |
| Age ratings | Trả lời bảng câu hỏi IARC: không có nội dung người dùng chia sẻ, không mua bán trong app |
| Packages | Kéo file `.msix` từ `Life-scanner-msix-store` vào |
| Store listings | Tiếng Việt (vi-VN), nội dung gợi ý bên dưới |

**Khả năng đặc biệt (runFullTrust):** Store sẽ hỏi lý do. Gợi ý:

> Life-scanner is a desktop (Win32) app packaged with MSIX. It needs full trust to save PDFs into a folder the user picks and to open that folder in File Explorer.

## 4. Nội dung trang Store gợi ý

**Mô tả ngắn:** Chụp giấy tờ bằng webcam, tự nắn phẳng, sắp trang và xuất PDF ngay trên máy. Không cần mạng, không gửi gì đi đâu.

**Mô tả:**

> Life-scanner biến webcam thành máy scan tài liệu.
>
> - Tự nhận khung giấy, nắn phẳng, làm trắng nền và xoay đúng chiều.
> - Chụp tự động khi tờ giấy đứng yên, thêm nhiều trang, kéo thả để sắp lại thứ tự.
> - Xuất PDF ngay, có thể giới hạn dung lượng file.
> - Lưu thẳng vào thư mục bạn chọn, tự chia thư mục theo loại thủ tục, tự đánh số, không bao giờ ghi đè.
> - Sổ thành tích đếm số thủ tục và số trang đã scan.
> - Chạy hoàn toàn offline: ảnh và tài liệu không rời khỏi máy.

**Tính năng nổi bật (Features):** Tự nhận và nắn phẳng giấy · Chụp tự động · Xuất PDF giới hạn dung lượng · Lưu theo thủ tục · Chạy offline

**Ảnh chụp màn hình:** ít nhất 1 ảnh, khuyên 1366×768 hoặc 1920×1080, PNG.

Câu khẩu hiệu trong app có từ lóng; trên trang Store nên dùng lời lẽ như trên để qua duyệt nhanh.
