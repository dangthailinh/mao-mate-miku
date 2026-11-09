# Asset và phạm vi bản public

## Model Miku tải riêng

Nguồn: **Tomaru / 止丸**, [BOOTH item 3226395](https://booth.pm/en/items/3226395). Tác giả cho dùng hình ảnh/video và tùy chỉnh trong phạm vi điều khoản, nhưng cấm phân phối lại dữ liệu model. Hatsune Miku thuộc Crypton Future Media, Inc.; đọc điều kiện trên trang nguồn/Piapro trước khi sử dụng.

Repo không chứa `hatsune-miku.vrm`, nguồn model Tomaru hoặc avatar mặc định của Mate Engine. Tải model từ tác giả, dùng cục bộ và đặt tại `AvatarAssets/hatsune-miku.vrm`. Không đưa VRM vào fork/PR/release. Model khác có thể không tương thích mọi tùy chỉnh dành riêng cho Miku.

## Chuyển động bắt buộc để build hiện tại

Script `MaoMateBuild.BuildWindowsApp` gọi kiểm tra advanced locomotion trước khi build. Cần **10 FBX Humanoid hợp lệ**, một clip tương ứng mỗi file, tại:

`Assets/MaoIntegration/Motions/FemaleLocomotion/`

| Tên file chính xác | Chuyển động |
|---|---|
| `idle.fbx` | Đứng chờ |
| `walking.fbx` | Đi bộ |
| `running.fbx` | Chạy |
| `left turn.fbx` | Quay trái |
| `right turn.fbx` | Quay phải |
| `left strafe walk.fbx` | Đi ngang trái |
| `right strafe walk.fbx` | Đi ngang phải |
| `left strafe.fbx` | Di chuyển ngang trái |
| `right strafe.fbx` | Di chuyển ngang phải |
| `jump.fbx` | Nhảy |

Bộ FBX trên máy người duy trì không có hồ sơ nguồn/giấy phép phân phối đủ rõ nên không công khai. Bạn có thể tự tạo hoặc lấy bộ Humanoid từ nguồn được cấp quyền, chẳng hạn tìm chuyển động phù hợp trên [Mixamo](https://www.mixamo.com/), kiểm tra [FAQ của Adobe](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html) và điều kiện tại thời điểm tải. Không có liên kết tải lại bộ riêng của người duy trì. Tên file đúng không bảo đảm kết quả giống ảnh; Unity phải xác nhận rig/clip và bạn cần thử chuyển động.

`AvatarAssets/MikuPoses.json`, controller và motion Macaron dùng trong bước build vẫn có trong source. Một số tham chiếu asset riêng có thể bị thiếu trên clone mới: chưa có build Unity xác nhận sau khi lọc.

## Các asset riêng khác đã bỏ khỏi bản public

- FBX cử chỉ/phản ứng/rơi/chơi đàn trong `Assets/MaoIntegration/Motions`.
- Raw pose/animation/icon trong `Assets/Resources/MikuPoses` và prop `Assets/Resources/MaoGuitar`.
- Ghế/macaron GLB riêng trong `Assets/StreamingAssets/MaoSeating`.
- MP3 trong `Assets/MaoIntegration/DancePacks` và các gói `ExportedDancePacks` có nhạc.
- Model cục bộ, cache, output build, log, cấu hình người dùng và credentials.

Các mục trên có thể làm tính năng tương ứng thiếu hoặc không chạy. Chúng không được phát hành như asset tự do. Bản public dùng lịch sử Git mới để các file này cũng không tồn tại trong commit cũ. Bản đầy đủ của người duy trì được lưu riêng trong kho private.

## Attribution

Giữ nguyên `LICENSE.md`, `NOTICE.txt`, giấy phép trong các thư mục thành phần và metadata đi kèm. Nguồn nền: [shinyflvre/Mate-Engine](https://github.com/shinyflvre/Mate-Engine), snapshot upstream `2c5ea6b8f4cf5e1773a0816b46d9267cda5174d4`. Bản fork này không đại diện cho tác giả Mate Engine, Tomaru hoặc Crypton.
