# Đóng góp

Mở issue với bước tái hiện, phiên bản Windows/Unity/Node và kết quả mong đợi. Với lỗi build, thêm lỗi đầu tiên trong Unity Console sau khi xóa dữ liệu riêng tư.

Tạo branch, giới hạn PR vào một thay đổi rõ ràng. Giữ giấy phép và attribution. Không commit model Tomaru, raw motion chưa có quyền phân phối, nhạc thương mại, `Library`, output build, log, `.env`, khóa hoặc lịch sử hội thoại.

Trước PR, chạy `scripts/Check-Setup.ps1` và test phù hợp. Thay đổi bridge cần `npm.cmd test` trong `Tools/MikuHarness`; thay đổi Unity cần compile/test Unity và kiểm tra trên bản Windows khi có môi trường. Ghi rõ phần chưa kiểm thử. Không đổi permission mode hoặc giảm các bước duyệt để làm test “pass”.

Nguồn public đang thiếu các asset bên ngoài: xem `docs/ASSETS.md`. Không dùng screenshot bản cài của người duy trì làm bằng chứng cho một bản build khác.
