# User APIs

---

# Part 1: Admin User Management

Base URL: `/api/v1/admin`

Tất cả các API trong Part 1 yêu cầu quyền **Admin** (`Authorize(Policy = "AdminOnly")`).

---

## 1.1. List All Users (Cursor-based)

Liệt kê tất cả users (trừ tài khoản Admin), phân trang dạng cursor, sắp xếp theo thời gian tạo mới nhất.

```
GET /users?pageSize=50&cursorCreatedAt=2025-01-01T00:00:00Z&cursorId=3fa85f64-5717-4562-b3fc-2c963f66afa6
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| pageSize | int | 50 | Số item mỗi trang (max 50) |
| cursorCreatedAt | datetime | (null) | `CreatedAt` của item cuối cùng từ page trước — dùng cho cursor |
| cursorId | guid | (null) | `Id` của item cuối cùng từ page trước — dùng cho cursor |

### Cursor Pagination

- Gọi lần đầu: **không gửi cursor** → lấy page đầu tiên
- Từ page thứ 2: copy `nextCursorCreatedAt` và `nextCursorId` từ response trước đó sang `cursorCreatedAt` và `cursorId`
- Sort: `CreatedAt DESC, Id DESC` — user mới nhất lên đầu
- Nếu `hasMore = false` → hết dữ liệu

### Response (200)

```json
{
  "items": [
    {
      "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "email": "admin@example.com",
      "userName": "admin",
      "emailConfirmed": true,
      "organizationUnitNames": ["IT Department", "HR"]
    }
  ],
  "nextCursorCreatedAt": "2025-01-01T00:00:00Z",
  "nextCursorId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "hasMore": true
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| items | array | Danh sách users |
| items[].userId | guid | ID của user |
| items[].email | string | Email |
| items[].userName | string | Username |
| items[].emailConfirmed | bool | Email đã xác nhận? |
| items[].organizationUnitNames | string[] | Danh sách tên OU mà user thuộc về |
| nextCursorCreatedAt | datetime | Cursor cho page tiếp theo (giá trị `CreatedAt` của item cuối) |
| nextCursorId | guid | Cursor cho page tiếp theo (giá trị `Id` của item cuối) |
| hasMore | bool | Còn page tiếp theo không? |

---

## 1.2. Get User by ID

Lấy thông tin một user theo ID, kèm danh sách OU mà user thuộc về.

```
GET /users/{userId:guid}
```

**Response (200):**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "admin@example.com",
  "userName": "admin",
  "emailConfirmed": true,
  "organizationUnitNames": ["IT Department", "HR"]
}
```

**Response (404):** User không tồn tại.

---

## 1.3. Search Users by Email

Tìm kiếm người dùng theo email (trừ tài khoản Admin). Trả về **tối đa 10 kết quả** phù hợp nhất (sort theo email), kèm danh sách OU.

```
GET /users/search?email=xxx
```

### Query Parameters

| Param  | Type   | Default | Description |
|--------|--------|---------|-------------|
| email  | string | (empty) | Từ khoá tìm kiếm email (LIKE mù, không phân biệt hoa thường) |

### Search Behavior

| email value | Cách tìm |
|-------------|----------|
| `"admin"` | `NormalizedEmail` chứa "ADMIN" — tìm tất cả email có chứa "admin" |
| `"admin@ex"` | `NormalizedEmail` chứa "ADMIN@EX" — match bất kỳ vị trí nào |
| `""` (empty) | Trả về 10 users gần nhất (sort theo email) |

### Response (200)

```json
{
  "items": [
    {
      "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "email": "admin@example.com",
      "userName": "admin",
      "emailConfirmed": true,
      "organizationUnitNames": ["IT Department", "HR"]
    }
  ]
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| items | array | Danh sách users (tối đa 10) |
| items[].userId | guid | ID của user |
| items[].email | string | Email |
| items[].userName | string | Username |
| items[].emailConfirmed | bool | Email đã xác nhận? |
| items[].organizationUnitNames | string[] | Danh sách tên OU mà user thuộc về |

---

## 1.4. Performance

- 10k users — query DB trực tiếp, có index trên `NormalizedEmail` (gin_trgm) + `CreatedAt`
- Search email: `NormalizedEmail` dùng `gin_trgm_ops`, filter `LIKE` mù được index
- List users: cursor-based trên `CreatedAt DESC, Id DESC`, có index trên `CreatedAt`
- OU info: join `UserPositions` (có index composite `UserId + OUId`), batch query theo userIds
- Manager info: `UserPosition.ManagerId` (FK → `Users.Id`, index riêng) — eager-loaded qua `.Include(up => up.Manager)`
- Không dùng cache — response vẫn < 50ms với 10k users

---

# Part 2: User Profile

Base URL: `/api/v1/user`

Yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

Các endpoint này lấy thông tin từ user đang đăng nhập (qua JWT token).

---

## 2.1. Get My Profile

Lấy thông tin profile của user hiện tại, kèm danh sách positions và managers.

```
GET /me
```

**Response (200):**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "user@example.com",
  "userName": "user",
  "emailConfirmed": true,
  "positions": [
    {
      "id": "guid",
      "userId": "guid",
      "userName": "string",
      "email": "string",
      "ouId": "guid",
      "ouName": "string",
      "role": "Staff | Manager",
      "isPrimary": true,
      "joinedAt": "2024-01-01T00:00:00Z",
      "managerId": "guid | null",
      "managerName": "string | null",
      "managerEmail": "string | null"
    }
  ],
  "managers": [
    {
      "managerId": "guid",
      "managerName": "string",
      "managerEmail": "string",
      "ouId": "guid",
      "ouName": "string"
    }
  ]
}
```

---

## 2.2. Get My Positions

Lấy danh sách OU positions của user đang đăng nhập.

```
GET /me/positions
```

**Response (200):**
```json
[
  {
    "id": "guid",
    "userId": "guid",
    "userName": "string",
    "email": "string",
    "ouId": "guid",
    "ouName": "string",
    "role": "Staff | Manager",
    "isPrimary": true,
    "joinedAt": "2024-01-01T00:00:00Z",
    "managerId": "guid | null",
    "managerName": "string | null",
    "managerEmail": "string | null"
  }
]
```

---

## 2.3. Get My Managers

Lấy danh sách người quản lý (manager) của user hiện tại.
Kết quả được deduplicate theo `managerId` — nếu cùng một người quản lý ở nhiều OU thì chỉ xuất hiện 1 lần.

```
GET /me/managers
```

**Response (200):**
```json
[
  {
    "managerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "managerName": "Nguyễn Văn A",
    "managerEmail": "a@example.com",
    "ouId": "guid",
    "ouName": "Phòng IT"
  }
]
```

---

## 2.4. Get My OUs

Lấy danh sách các OU mà user hiện tại thuộc về.

```
GET /me/ous
```

**Response (200):**
```json
[
  {
    "ouId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "ouName": "Phòng IT"
  }
]
```

---

## 2.5. Get User Managers

Lấy danh sách người quản lý của một user khác (theo userId).

```
GET /users/{userId:guid}/managers
```

**Response (200):** `UserManagerDto[]` (cấu trúc giống 2.3)

**Response (400):** User không tồn tại.

---

## 2.6. Get User Positions

Lấy danh sách vị trí OU của một user khác (theo userId).

```
GET /users/{userId:guid}/positions
```

**Response (200):** `UserPositionDto[]` (cấu trúc giống 2.2)

**Response (400):** User không tồn tại.

---

## 2.7. Get Managers in OU

Lấy danh sách user có role `Manager` trong một OU (dùng cho dropdown chọn manager).

```
GET /org/{ouId:guid}/managers
```

**Response (200):**
```json
[
  {
    "userId": "guid",
    "userName": "string",
    "email": "string"
  }
]
```

---

## 2.8. Get OU Tree (for current user)

Trả về toàn bộ cây OU dạng tree (giống admin), kèm flag `isMember`/`isManager` cho user hiện tại. UI có thể dùng `isMember` để disable các OU không thuộc về user.

```
GET /org/tree
```

**Response (200):**
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "Root OU",
    "code": null,
    "level": 0,
    "totalUsers": 10,
    "totalDatasets": 3,
    "isMember": false,
    "isManager": false,
    "children": [
      {
        "id": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
        "name": "Phòng IT",
        "code": "IT",
        "level": 1,
        "totalUsers": 5,
        "totalDatasets": 1,
        "isMember": true,
        "isManager": true,
        "children": []
      }
    ]
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID của OU |
| name | string | Tên OU |
| code | string? | Mã code OU (VD: "IT", "HR") |
| level | int | Độ sâu trong cây (root = 0) |
| totalUsers | int | Tổng số users trong OU |
| totalDatasets | int | Tổng số datasets trong OU |
| isMember | bool | User hiện tại có position trong OU này không |
| isManager | bool | User hiện tại có role Manager trong OU này không |
| children | array | Các OU con (đệ quy, cấu trúc giống node cha) |

---

## Common Error Responses

| Status | Description |
|--------|-------------|
| 401    | Unauthorized — Chưa đăng nhập |
| 404    | Not Found — User không tồn tại |
