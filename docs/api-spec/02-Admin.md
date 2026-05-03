# Admin API

Base URL: `/api/v1/admin`

Tất cả các API trong nhóm này yêu cầu quyền **Admin** (`Authorize(Policy = "AdminOnly")`). Dùng cho Dashboard quản trị hệ thống.

---

## Frontend: Kiểm tra quyền Admin

Frontend có thể biết user hiện tại có phải Admin hay không qua các API auth:

### Cách 1: Từ response Login / Register

Sau khi gọi `POST /api/auth/login` hoặc `POST /api/auth/register`, response trả về trường `roles`:

```json
{
  "email": "admin@example.com",
  "userName": "admin",
  "isAuthenticated": true,
  "roles": ["Admin", "User"]
}
```

Kiểm tra bằng `roles.includes("Admin")`.

### Cách 2: Từ API `/api/auth/me`

Khi app khởi động, gọi `GET /api/auth/me` để lấy thông tin user (kể cả khi đã có cookie):

```json
{
  "user": "admin",
  "email": "admin@example.com",
  "isAuthenticated": true,
  "roles": ["Admin", "User"]
}
```

**Frontend logic:**
```javascript
const isAdmin = (user?.roles ?? []).includes('Admin');
```
Dựa vào `isAdmin` để show/hide các UI element như Admin Dashboard, nút quản lý người dùng, v.v.

---

## 1. Organization & Personnel

### 1.1. Get Org Tree

Lấy toàn bộ cây tổ chức (OU).

```
GET /org/tree
```

**Response (200):**
```json
[
  {
    "id": "guid",
    "name": "string",
    "code": "string | null",
    "level": 0,
    "totalUsers": 0,
    "totalDatasets": 0,
    "children": [
      { "...": "..." }
    ]
  }
]
```

---

### 1.2. Get Org By ID

Lấy một OU theo ID (kèm cây con).

```
GET /org/{ouId:guid}
```

**Response (200):**
```json
{
  "id": "guid",
  "name": "string",
  "code": "string | null",
  "level": 0,
  "totalUsers": 0,
  "totalDatasets": 0,
  "children": []
}
```

**Response (404):** OU không tồn tại.

---

### 1.3. Get Users in OU

Lấy danh sách user trực tiếp trong một OU (không bao gồm OU con).

```
GET /org/{ouId:guid}/users
```

**Response (200):**
```json
[
  {
    "userId": "guid",
    "email": "string",
    "userName": "string",
    "ouId": "guid",
    "ouName": "string",
    "role": "Staff | Manager",
    "isPrimary": true,
    "joinedAt": "2024-01-01T00:00:00Z",
    "managerName": "string | null"
  }
]
```

---

### 1.4. Get Users in OU and Children

Lấy user trong OU và tất cả OU con.

```
GET /org/{ouId:guid}/users/tree
```

**Response (200):** `OrgUserDto[]` (cấu trúc giống 1.3)

---

### 1.5. Assign User to OU

Gán user vào một OU. Mỗi user **chỉ có 1 primary position duy nhất**.

> **Lưu ý:** Tài khoản **Admin** không thể được gán vào OU — chỉ dùng để quản trị hệ thống.

```
POST /users/{userId:guid}/assign
```

**Request Body:**
```json
{
  "ouId": "guid",
  "role": "Staff",       // Staff = 0, Manager = 1
  "isPrimary": true,
  "managerId": "guid"     // required nếu role=Staff, optional nếu role=Manager
}
```

**Business Logic:**

| isPrimary | Hành vi |
|-----------|---------|
| `true` | Tất cả `UserPosition` khác của user bị set `IsPrimary = false`. **Tất cả Dataset của user đó được tự động chuyển `OUId` sang OU mới này.** |
| `false` | Chỉ gán user vào OU, không ảnh hưởng primary hay Dataset. |

| Field | Mô tả |
|-------|-------|
| `managerId` | **Bắt buộc** nếu `role = Staff` — Staff phải có manager. **Optional** nếu `role = Manager`. Nếu có, phải là `UserId` của một user có role `Manager` trong cùng OU. |

**Response (200):** OK - Gán thành công.

**Response (400):** User đã tồn tại trong OU hoặc dữ liệu không hợp lệ (VD: Staff không có manager, hoặc `managerId` không phải Manager của OU đó).

---

### 1.6. Remove User from OU

Xoá user khỏi OU.

```
DELETE /users/{userId:guid}/ou/{ouId:guid}
```

**Response (200):** OK - Xoá thành công.

**Response (404):** User position không tồn tại.

---

### 1.7. Create OU

Tạo một đơn vị tổ chức mới (có thể là root OU hoặc OU con).

```
POST /org
```

**Request Body:**
```json
{
  "name": "Phòng IT",
  "code": "IT",
  "parentId": "guid | null"    // null = root OU
}
```

**Response (201):** `OrgTreeDto` - OU vừa tạo.

**Response (400):** Parent OU không tồn tại.

---

### 1.8. Update OU

Cập nhật tên và code của OU.

```
PUT /org/{ouId:guid}
```

**Request Body:**
```json
{
  "name": "Phòng Công nghệ Thông tin",
  "code": "CNTT"
}
```

**Response (200):** `OrgTreeDto` - OU sau khi cập nhật.

**Response (404):** OU không tồn tại.

---

### 1.9. Move OU (Reparent)

Di chuyển OU sang parent khác (reparent). Giữ nguyên toàn bộ subtree con của OU.

```
POST /org/{ouId:guid}/move
```

**Request Body:**
```json
{
  "parentId": "guid | null"
}
```

| Field | Type | Description |
|-------|------|-------------|
| parentId | guid? | Parent mới. `null` = root |

**Validation:**

| Rule | Error (400) |
|------|-------------|
| `parentId != null` nhưng không tồn tại | `Parent OU not found` |
| `parentId == ouId` (self) | `Cannot set self as parent` |
| `parentId` là con/cháu của chính OU | `Cannot set descendant as parent (circular dependency)` |

**Operation:**
1. Validate parent (exists, not self, not descendant)
2. Cập nhật `ParentId`, tính lại `Path` + `Level` cho OU và toàn bộ subtree
3. Save

**Response (200):** `OrgTreeDto` - OU sau khi move.

**Response (400):** Validation failed.

**Response (404):** OU không tồn tại.

---

### 1.10. Delete OU

Xoá cứng (hard delete) một OU. **Không cascade xoá con** — thay vào đó reparent children lên root.

```
DELETE /org/{ouId:guid}
```

**Cascade Behavior:**

| Đối tượng bị ảnh hưởng | Hành động | Cơ chế |
|--------------------------|-----------|--------|
| **OU con trực tiếp** | Reparent lên root (`ParentId = null`, `Level = 0`, `Path` được tính lại cho toàn bộ subtree) | Manual |
| **UserPosition trong OU** | Xoá cứng khỏi DB | FK Cascade |
| **Dataset trong OU** | `OUId = null`, `IsPublicToUnit = false` — dataset trở thành standalone | Manual (FK Restrict) |
| **AccessShare đến OU** | `ShareToOUId = null` — share được giữ lại, chỉ xoá tham chiếu OU | Manual |
| **SystemStatistics của OU** | Xoá cứng khỏi DB | FK Cascade |

> **Ví dụ:** Xoá OU "IT" → "IT-BE" và "IT-FE" (con trực tiếp) trở thành root OU mới, giữ nguyên subtree của chúng. UserPositions trong "IT" bị xoá vĩnh viễn.

**Response (200):** OK - Xoá thành công.

**Response (404):** OU không tồn tại.

---

## 2. Dashboard Statistics

### 2.1. Get System Summary

Thống kê tổng quan toàn hệ thống.

```
GET /stats/summary
```

**Response (200):**
```json
{
  "totalDatasets": 0,
  "totalDocuments": 0,
  "totalStorageDisplay": "1.2 GB",
  "totalOUs": 0,
  "totalUsers": 0
}
```

---

### 2.2. Get Storage Chart

Biểu đồ phân bổ storage theo từng OU (dạng **flat list** — dùng cho Pie/Bar chart).

```
GET /stats/storage-chart
```

**Response (200):**
```json
[
  {
    "ouId": "guid | null",
    "ouName": "string",
    "datasetCount": 0,
    "documentCount": 0,
    "storageDisplay": "500 MB",
    "storageBytes": 524288000,
    "percentage": 25.5
  }
]
```

---

### 2.3. Get Storage Tree

Biểu đồ phân bổ storage theo từng OU (dạng **cây phân cấp** — dùng cho Treemap / Sunburst chart).

```
GET /stats/storage-tree
```

**Response (200):**
```json
[
  {
    "id": "guid",
    "name": "string",
    "code": "string | null",
    "level": 0,
    "totalDatasets": 0,
    "totalDocuments": 0,
    "totalStorageBytes": 524288000,
    "storageDisplay": "500 MB",
    "children": []
  }
]
```

**Gợi ý Chart cho Frontend:**

| Chart | Mô tả | Khi nào dùng |
|-------|-------|-------------|
| **Treemap** | Hình chữ nhật lồng nhau, kích thước = storage bytes | **Khuyên dùng** — trực quan cho storage, user thấy ngay OU nào chiếm nhiều nhất, hierarchy thể hiện qua nesting |
| **Sunburst** | Radial, vòng trong = root, vòng ngoài = con cháu | Khi cần hiển thị nhiều level hierarchy trên 1 chart |
| **Icicle** | Hình chữ nhật xếp dọc theo chiều level | Khi cần dạng top-down, dễ so sánh theo cột |

> **Lưu ý:** Stats của OU cha **đã bao gồm** stats của OU con (aggregated). Với Treemap, điều này tự nhiên vì children nằm trong parent rectangle.

---

### 2.4. Get Stats by OU

Thống kê chi tiết cho một OU cụ thể.

```
GET /stats/ou/{ouId:guid}
```

**Response (200):** Tuỳ theo service trả về.

**Response (404):** OU không tồn tại.

---

### 2.5. Recalculate Stats

Kích hoạt tính toán lại toàn bộ thống kê (manual trigger).

```
POST /stats/recalculate
```

**Response (200):**
```json
{
  "message": "Statistics recalculated successfully"
}
```

---

## 3. Dataset Management

### 3.1. Get All Datasets

Danh sách tất cả datasets (có phân trang).

```
GET /datasets?page=1&pageSize=20
```

**Query Parameters:**

| Param    | Type  | Default | Description  |
|----------|-------|---------|--------------|
| page     | int   | 1       | Trang hiện tại |
| pageSize | int   | 20      | Số item mỗi trang |

**Response (200):**
```json
{
  "items": [
    {
      "id": "guid",
      "name": "string",
      "ownerName": "string",
      "ouName": "string | null",
      "itemCount": 0,
      "documentCount": 0,
      "storageDisplay": "string",
      "isPublicToUnit": true,
      "createdAt": "2024-01-01T00:00:00Z",
      "updatedAt": "2024-01-01T00:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 100,
  "totalPages": 5
}
```

---

### 3.2. Get Dataset Items

Lấy cây thư mục / items của một dataset.

```
GET /datasets/{datasetId:guid}/items?parentId=null
```

**Query Parameters:**

| Param    | Type     | Default | Description                           |
|----------|----------|---------|---------------------------------------|
| parentId | guid?    | null    | Lọc theo thư mục cha (null = root)    |

**Response (200):**
```json
[
  {
    "id": "guid",
    "name": "string",
    "itemType": "Folder | Document",
    "hasChildren": true,
    "sizeDisplay": "string | null",
    "sizeBytes": 1024,
    "childCount": 0
  }
]
```

---

### 3.3. Transfer Dataset Ownership

Chuyển quyền sở hữu dataset sang user khác.

```
POST /datasets/{datasetId:guid}/transfer-owner
```

**Request Body:**
```json
{
  "newOwnerUserId": "guid"
}
```

**Response (200):** OK - Chuyển thành công.

**Response (400):** Dataset hoặc user mới không tồn tại.

---

### 3.4. Get Dataset Shares

Danh sách access shares của một dataset.

```
GET /datasets/{datasetId:guid}/shares
```

**Response (200):**
```json
[
  {
    "id": "guid",
    "datasetId": "guid",
    "datasetItemId": "guid | null",
    "shareToUserId": "guid | null",
    "shareToUserName": "string | null",
    "shareToOUId": "guid | null",
    "shareToOUName": "string | null",
    "permissionMask": 1,
    "permissionDisplay": "string",
    "grantedBy": "guid",
    "grantorName": "string",
    "grantedAt": "2024-01-01T00:00:00Z"
  }
]
```

**PermissionMask values (DatasetPermissions - Flags enum):**

| Value | Name        | Description      |
|-------|-------------|------------------|
| 0     | None        | Không có quyền   |
| 1     | Read        | Xem              |
| 2     | Update      | Cập nhật         |
| 4     | Delete      | Xoá              |
| 8     | Share       | Chia sẻ          |
| 3     | Collaborate | Read + Update    |
| 15    | FullControl | Read + Update + Delete + Share |

---

## Common Error Responses

| Status | Description               |
|--------|---------------------------|
| 401    | Unauthorized - Chưa đăng nhập |
| 403    | Forbidden - Không phải Admin |
