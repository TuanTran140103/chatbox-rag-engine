# Authentication API

Hệ thống hỗ trợ cơ chế đăng nhập **Hybrid (Hỗn hợp)**: Đăng nhập bằng tài khoản cục bộ (Email/Password) và Đăng nhập tập trung qua SSO/Mạng xã hội.

## 1. Giao diện Đăng nhập (Frontend UI Guidance)

Để đảm bảo trải nghiệm người dùng tốt nhất, trang Đăng nhập nên được thiết kế gồm 2 phần:

1.  **Local Login**: Form nhập `Email` và `Password`. (Dành cho tài khoản nội bộ).
2.  **Social/SSO Login**: Một nút bấm lớn, nổi bật với nội dung **"Login via Social / SSO"**. (Dành cho đăng nhập qua Google, GitHub, hoặc tài khoản công ty).

---

## 2. API Đăng nhập Social / SSO

Khi người dùng click vào nút **"Login via Social / SSO"**, Frontend thực hiện chuyển hướng trình duyệt (không dùng fetch/axios).

**Endpoint:** `GET /api/auth/login-oidc?returnUrl=<optional_redirect_url>`

**Frontend Implementation:**
```javascript
// Gán trực tiếp vào href của nút bấm
const loginSso = () => {
  const returnUrl = encodeURIComponent(window.location.pathname);
  window.location.href = `/api/auth/login-oidc?returnUrl=${returnUrl}`;
};
```

**Mô tả luồng:**
1.  Frontend chuyển hướng sang Backend.
2.  Backend đẩy người dùng sang trang **Authentik Hub**.
3.  Tại Authentik, người dùng chọn các phương thức như Google, GitHub, v.v.
4.  Sau khi xong, Backend tự động xử lý: **Tạo tài khoản nếu mới** hoặc **Đăng nhập nếu đã có**.
5.  Backend trả người dùng về trang `returnUrl` trên Frontend.

---

## 3. API Đăng ký & Đăng nhập Mật khẩu

Dành cho phần **Local Login**.

### 3.1. Đăng ký (Register)
**Endpoint:** `POST /api/auth/register`
**Body:** `{ "email": "...", "password": "...", "confirmPassword": "..." }`

**Response (200 OK):**
```json
{
  "email": "user@example.com",
  "userName": "user@example.com",
  "isAuthenticated": true,
  "roles": ["User"]
}
```

### 3.2. Đăng nhập (Login)
**Endpoint:** `POST /api/auth/login`
**Body:** `{ "email": "...", "password": "...", "rememberMe": true }`

**Response (200 OK):**
```json
{
  "email": "admin@example.com",
  "userName": "admin",
  "isAuthenticated": true,
  "roles": ["Admin", "User"]
}
```

---

## 4. Kiểm tra trạng thái & Lấy thông tin (Get Me)

Frontend nên gọi API này ngay khi ứng dụng khởi chạy để biết người dùng đã đăng nhập hay chưa.

**Endpoint:** `GET /api/auth/me`

**Response (200 OK):**
```json
{
  "user": "admin",
  "email": "admin@example.com",
  "isAuthenticated": true,
  "roles": ["Admin", "User"]
}
```

**Response (401 Unauthorized):**
Người dùng chưa đăng nhập.

---

## 5. Đăng xuất (Logout)

Hệ thống sử dụng cơ chế **OIDC Front-channel Logout**. Trình duyệt của người dùng sẽ thực hiện một chuỗi chuyển hướng để xóa session tại cả Ứng dụng và Authentik (IdP).

**Endpoint:** `GET /api/auth/logout`

**Frontend Implementation:**
> **CẢNH BÁO:** Không sử dụng `axios` hoặc `fetch`. Bắt buộc phải chuyển hướng trình duyệt toàn trang.

```javascript
const logout = () => {
  // Chuyển hướng trình duyệt trực tiếp tới API logout của Backend
  window.location.href = '/api/auth/logout';
};
```

**Mô tả luồng hoạt động:**
1.  **Frontend**: Gán `window.location.href` tới `/api/auth/logout`.
2.  **Backend**: Thực hiện SignOut local và trả về lệnh chuyển hướng (302) tới Authentik kèm theo `id_token_hint` và `post_logout_redirect_uri`.
3.  **Authentik**: Xóa session của người dùng, sau đó chuyển hướng trình duyệt quay lại Backend (`/signout-callback-oidc`).
4.  **Backend Callback**: Nhận phản hồi từ Authentik và thực hiện bước chuyển hướng cuối cùng về trang login của Frontend (`/login`).

**Yêu cầu cấu hình tại Authentik (IdP):**
- **Logout Method**: Phải cấu hình là `Front-channel`.
- **Redirect URIs**: Phải bao gồm URL callback đăng xuất của Backend (ví dụ: `https://localhost:7128/signout-callback-oidc`).

**Kết quả mong đợi:**
- Session tại ứng dụng bị xóa.
- Session tại Authentik bị xóa (Khi login lại, người dùng sẽ thấy màn hình đăng nhập để chọn tài khoản khác).
- Trình duyệt tự động quay trở lại trang `/login` của Frontend.

---

## Flow Hoạt động (Visual)

```
       [ TRANG LOGIN FRONTEND ]
      ┌─────────────────────────┐
      │  Email: [            ]  │
      │  Pass:  [            ]  │
      │      ( Button Login )   │
      │                         │
      │      ─── OR ───         │
      │                         │
      │  ( LOGIN VIA SOCIAL/SSO)│ ───► Chuyển hướng sang Backend
      └─────────────────────────┘      (/api/auth/login-oidc)
                                               │
                                               ▼
                                      ┌─────────────────┐
                                      │  AUTHENTIK HUB  │
                                      │ (Google, GitHub)│
                                      └────────┬────────┘
                                               │
                                               ▼
      ┌─────────────────────────┐      ┌─────────────────┐
      │   TRANG CHỦ FRONTEND    │ ◄─── │     BACKEND     │
      │    (Authenticated)      │      │ (Auto Register) │
      └─────────────────────────┘      └─────────────────┘
```

## Lưu ý cho Frontend
- **Terminology**: Tuyệt đối không sử dụng từ "OIDC" trên giao diện. Hãy dùng "Social Login", "SSO", hoặc "Company Account".
- **Auto Signup**: Người dùng không cần bấm "Đăng ký" nếu họ dùng Social Login. Hệ thống sẽ tự động tạo tài khoản trong lần đầu tiên họ "Login via Social".
- **Cookie**: Backend sử dụng Cookie bảo mật (`MarkdownGenQAs.Auth`). Frontend không cần lưu Token vào LocalStorage. Mọi Request sau đó trình duyệt sẽ tự gửi kèm Cookie.
