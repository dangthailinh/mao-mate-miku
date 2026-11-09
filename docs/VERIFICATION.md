# Kiểm chứng bản source cộng đồng — 09/11/2026

| Kiểm tra | Kết quả |
|---|---|
| Cài dependency đúng lockfile | `npm.cmd ci --ignore-scripts --no-audit --no-fund` thành công; 537 packages |
| Test cầu nối trên bản export | `npm.cmd test`: **18/18 đạt**, Node 24.18.0 |
| Script đầu vào | Chạy thật bằng Windows PowerShell; báo thiếu Unity + VRM + 10 FBX đúng với bản export, exit 1 |
| Tính toàn vẹn source giữ lại | Đối chiếu SHA-256 9.584 file source/asset/dependency config giữ lại với bản private: không có thay đổi ngoài ý muốn |
| Giấy phép chính | `LICENSE.md` giữ nguyên so với snapshot upstream |
| Dấu hiệu secrets trong file chuẩn bị commit | Quét các file text, kể cả Unity YAML lớn; 29 kết quả trong 3 file test, đã kiểm tra là fixture giả |
| Asset/data riêng | Không đưa VRM, raw motion/pose/prop riêng, gói nhạc, log, credentials hoặc runtime cá nhân vào lịch sử public mới |
| Ảnh ứng dụng | Hai frame render thực từ app Windows đã cài; preview ngoại hình báo `failures=0` |
| Bảng quyền bản cài | Diagnostic xác nhận 9 công cụ, Harness đã đóng gói; không cấp quyền hay thực thi tác vụ trong lượt kiểm tra |

## Giới hạn

- **Chưa build/import lại bản source public bằng Unity**: máy duy trì đã gỡ Editor để giải phóng dung lượng. Các asset riêng bị loại cũng cần được người build bổ sung. Không có installer được phát hành trong lần này.
- Ảnh là bản cài đầy đủ trên máy duy trì, không chứng minh parity của clone public. Không công khai ảnh bảng quyền có workspace cá nhân.
- Lượt chụp có provider 503/timeout; không xác nhận chat trực tuyến hoặc giọng nói hoạt động ở lần kiểm tra này.
- Test Node kiểm tra protocol/policy/lifecycle; không thay thế integration test Unity hay kiểm thử quyền trên máy mới.
- Quét secrets theo mẫu và rà soát file không phải cam kết loại trừ mọi chuỗi nhạy cảm có thể có. Không có credentials thật được phát hiện trong những kết quả đã rà.
- Dependency graph giữ nguyên phiên bản pin của dự án; không thực hiện nâng dependency hoặc tuyên bố không còn advisory trong lần công khai này. Xem ghi chú kỹ thuật trong `MIKU_PERMISSIONS.md`.
