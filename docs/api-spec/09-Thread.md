# Thread API

Base URL: `/api/v1/threads`

Tất cả API trong nhóm này yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

Thread dùng để quản lý các phiên hội thoại/hỏi đáp từ service ngoài (ví dụ: OpenAI, Claude), hỗ trợ soft delete và tìm kiếm theo tiêu đề. `threadId` là ID từ service ngoài do client truyền lên.

---

## 1. List Threads

Liệt kê thread, sắp xếp theo `CreatedAt` giảm dần, tối đa 20 thread. Hỗ trợ lọc theo Id hoặc Title.

```
GET /api/v1/threads?id={guid}&title={string}
```

### Query Parameters

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| id | guid | No | Lọc theo Id chính xác |
| title | string | No | Tìm kiếm theo title (ILIKE, không phân biệt hoa thường) |

### Response (200)

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "threadId": "thread_abc123xyz",
    "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
    "title": "Hỏi về báo cáo tài chính Q1",
    "createdAt": "2025-01-15T10:30:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID thread (PK local) |
| threadId | string | ID từ service ngoài (ví dụ: OpenAI thread ID) |
| userId | guid | ID người tạo |
| title | string | Tiêu đề thread |
| createdAt | datetime | Thời gian tạo |
| updatedAt | datetime | Thời gian cập nhật cuối |

---

## 2. Get Thread Detail

Lấy thông tin chi tiết một thread.

```
GET /api/v1/threads/{id:guid}
```

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "threadId": "thread_abc123xyz",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "title": "Hỏi về báo cáo tài chính Q1",
  "createdAt": "2025-01-15T10:30:00Z",
  "updatedAt": "2025-01-15T10:30:00Z"
}
```

### Response (404)

```json
{
  "error": "Thread not found"
}
```

---

## 3. Create Thread

Tạo thread mới với `threadId` từ service ngoài, tự động gán `userId` từ user đang đăng nhập.

```
POST /api/v1/threads
```

### Request Body

```json
{
  "threadId": "thread_abc123xyz",
  "title": "Hỏi về báo cáo tài chính Q1"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| threadId | string | Yes | ID từ service ngoài (ví dụ: OpenAI thread ID) |
| title | string | Yes | Tiêu đề thread |

### Response (201)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "threadId": "thread_abc123xyz",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "title": "Hỏi về báo cáo tài chính Q1",
  "createdAt": "2025-01-15T10:30:00Z",
  "updatedAt": "2025-01-15T10:30:00Z"
}
```

### Response Fields

Như Get Thread Detail.

---

## 4. Update Thread

Cập nhật tiêu đề thread. Chỉ cho phép update `title`.

```
PUT /api/v1/threads/{id:guid}
```

### Request Body

```json
{
  "title": "Hỏi về báo cáo tài chính Q1 (đã chỉnh sửa)"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| title | string | Yes | Tiêu đề mới |

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "threadId": "thread_abc123xyz",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "title": "Hỏi về báo cáo tài chính Q1 (đã chỉnh sửa)",
  "createdAt": "2025-01-15T10:30:00Z",
  "updatedAt": "2025-01-15T10:35:00Z"
}
```

### Response (404)

```json
{
  "error": "Thread not found"
}
```

---

## 5. Delete Thread

Xoá mềm thread (set `IsDeleted = true`).

```
DELETE /api/v1/threads/{id:guid}
```

### Response (204)

Không có nội dung trả về.

### Response (404)

```json
{
  "error": "Thread not found"
}
```
