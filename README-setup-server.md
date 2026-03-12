# WireGuard RelayServer — Auto Setup Script

## Cách sử dụng

1. **Clone repo về VPS/server mới:**

```bash
git clone <repo-url> ~/wg-relay-server
cd ~/wg-relay-server
```

2. **Chạy script auto setup:**

```bash
bash setup-server.sh
```

> Script sẽ tự động:
> - Cài WireGuard, Python, UFW
> - Tạo config WireGuard nếu chưa có
> - Enable IP forwarding
> - Mở port firewall
> - Cài dashboard Python dependencies
> - Tạo systemd service dashboard
> - Khởi động WireGuard + dashboard

3. **Truy cập dashboard:**

```
http://<server-ip>:8080
```

## Lưu ý
- Nếu đã có WireGuard config, script sẽ không ghi đè.
- Nếu muốn reset dashboard, xóa file `/etc/systemd/system/wg-dashboard.service` rồi chạy lại script.
- Để thêm peer, dùng dashboard hoặc sửa config.

---

Nếu cần tuỳ biến thêm, hãy sửa script hoặc hỏi tôi!
