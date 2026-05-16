# Nginx Reverse Proxy

Nginx đóng vai trò **reverse proxy** duy nhất cho toàn bộ hệ thống, đứng giữa client (browser/mobile) và các services phía sau.

## 1. Kiến trúc

```
Browser (port 80)
    │
    ▼
┌─────────────────────────────────────┐
│         nginx (port 80)             │
│  localhost (nginx.dev.conf)         │
│                                     │
│  /api/*           → main-api:5184   │
│  /api/llm/*       → llm-service:8000│
│  /_auth/validate  → main-api (int)  │
│  /*               → frontend:3000   │
└─────────────────────────────────────┘
    │          │            │
    ▼          ▼            ▼
 main-api  llm-service   frontend
  :5184       :8000       :3000
```

## 2. Các Route Chính

| Route | Upstream | Mô tả |
|-------|----------|-------|
| `/api/` | `main-api:5184` | Backend API (CRUD, auth, dataset, document) |
| `/api/llm/` | `llm-service:8000` | LLM Service (có auth_request) |
| `/_auth/validate` | `main-api:5184` (internal) | Auth validation nội bộ cho LLM route |
| `/` | `frontend:3000` | Next.js frontend (có WebSocket HMR) |

## 3. Frontend → Backend API

Frontend gọi các API backend qua **cùng origin** (không CORS):

```javascript
// ❌ Không cần absolute URL
fetch('http://localhost:5184/api/datasets')  // Sai

// ✅ Dùng relative path, nginx sẽ proxy
fetch('/api/datasets')                        // Đúng
fetch('/api/auth/login')                       // Đúng
fetch('/api/v1/files/upload')                  // Đúng
```

Cấu hình nginx:
```nginx
location /api/ {
    proxy_pass http://main-api;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Cookie $http_cookie;
}
```

Client_max_body_size: **100MB** (cho phép upload file lớn).

## 4. Frontend → LLM Service (có Auth)

Route `/api/llm/` được bảo vệ bởi **auth_request** (internal subrequest đến backend):

```nginx
location /api/llm/ {
    auth_request /_auth/validate;
    auth_request_set $user_id $upstream_http_x_user_id;

    proxy_set_header X-User-Id "";
    proxy_set_header X-User-Id $user_id;
    proxy_pass http://llm-service/;
}
```

**Luồng hoạt động:**

1. Request `/api/llm/chat` kèm Cookie/Session
2. Nginx gửi subrequest internal `/_auth/validate` kèm Cookie
3. Backend kiểm tra session, trả về `200 OK` + header `X-User-Id` hoặc `401 Unauthorized`
4. Nếu 200: nginx forward request đến LLM service kèm `X-User-Id`
5. Nếu 401: nginx trả về 401 cho client

**Frontend gọi LLM service:**
```javascript
// Gọi như API bình thường, nginx lo auth
fetch('/api/llm/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt: '...' }),
});
```

## 5. Frontend → Frontend (Next.js + WebSocket)

Route `/` proxy đến Next.js dev server (port 3000) với WebSocket support cho HMR:

```nginx
location / {
    proxy_pass http://frontend;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
}
```

Cần WebSocket support để Next.js Hot Module Replacement hoạt động trong dev.

## 6. Cách chạy

```bash
# Start nginx cùng các services
docker compose -f Dockers/docker-compose.dev.yaml up -d

# Chỉ start nginx
docker compose -f Dockers/docker-compose.dev.yaml up -d nginx
```

Sau đó truy cập: `http://localhost` (port 80).

## 7. Lưu ý

- File config dev: `Dockers/nginx.dev.conf` (mount vào `/etc/nginx/nginx.conf`)
- File config prod: `Dockers/nginx.conf` (đã comment trong docker-compose.yaml)
- Nếu cần debug: kiểm tra log container `docker logs nginx-dev-proxy`
- Lỗi "can not modify /etc/nginx/conf.d/default.conf (read-only)" đã được fix bằng cách mount config vào đúng vị trí `/etc/nginx/nginx.conf`
