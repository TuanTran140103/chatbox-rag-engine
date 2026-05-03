# Document API

Base URL: `/api/v1/documents`

Tất cả các API trong nhóm này yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

> **Lưu ý:** Upload document **không có endpoint riêng** — việc upload file đã được tích hợp trong Dataset API (`POST /api/v1/user/me/datasets/{id}/create-item` với `type=1`). Các endpoint bên dưới dùng để quản lý document đã upload.

---

## Part 1: Document Management

Base URL: `/api/v1/documents`

---

### 1.1. Get Document Detail

Lấy thông tin chi tiết document, bao gồm nội dung OCR, Q&A, và Summary.

```
GET /api/v1/documents/{id:guid}/detail
```

### Response (200)

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "Bao-cao-2025.pdf",
  "status": "Successed",
  "processingTimeOcr": 120,
  "processingTimeGenQa": 60,
  "isOcred": true,
  "isQaGenerated": false,
  "ocrCount": 10,
  "genQaCount": 0,
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "categoryId": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
  "categoryName": "Báo cáo",
  "createdAt": "2025-01-01T00:00:00Z",
  "content": {
    "ocrMarkdown": "# Bao cao 2025\n\n## Section 1...",
    "qas": null,
    "summary": null
  }
}
```

### Response Fields

**Outer**

| Field | Type | Description |
|-------|------|-------------|
| id | guid | ID document |
| fileName | string | Tên file |
| status | string | Trạng thái xử lý |
| processingTimeOcr | int | Thời gian OCR (giây) |
| processingTimeGenQa | int | Thời gian gen Q&A (giây) |
| isOcred | bool | Đã OCR chưa |
| isQaGenerated | bool | Đã gen Q&A chưa |
| ocrCount | int | Số trang OCR |
| genQaCount | int | Số cặp Q&A |
| userId | guid? | User upload |
| categoryId | guid? | Category ID |
| categoryName | string? | Tên category |
| createdAt | datetime | Thời gian tạo |
| content | object | Nội dung chi tiết |

**Inner (DocumentContent)**

| Field | Type | Description |
|-------|------|-------------|
| ocrMarkdown | string? | Toàn bộ nội dung OCR dạng Markdown |
| qas | ChunkQAInfor[]? | Mảng các chunk Q&A |
| summary | string? | Tóm tắt document |

**ChunkQAInfor structure:**

| Field | Type | Description |
|-------|------|-------------|
| chunk_infor | ChunkInfo | Thông tin chunk |
| chunk_infor.type | TypeChunk | Loại chunk: Text, Table, Summary |
| chunk_infor.tokens_count | int | Số tokens |
| chunk_infor.title | string? | Tiêu đề chunk |
| chunk_infor.tittle_hirarchy | string? | Hierarchical title path |
| chunk_infor.content | string | Nội dung text của chunk |
| chunk_infor.content_summary | string? | Content summary (chỉ khi type=Summary) |
| chunk_infor.table_chunks | ChunkInfo[] | Sub-chunks cho table |
| qas | ChunkQA[] | Danh sách cặp câu hỏi-trả lời |
| qas[].question | string | Câu hỏi |
| qas[].answer | string | Câu trả lời (trích xuất từ document) |
| qas[].category | string? | Thể loại câu hỏi |

### Response (404)

```json
"File not found"
```

---

### 1.2. Get OCR Content

Lấy chỉ nội dung OCR của document.

```
GET /api/v1/documents/{id:guid}/content/ocr
```

### Response (200)

Trả về chuỗi Markdown thuần túy (text/plain).

```
# Bao cao 2025

## Section 1
Content here...
```

### Response (404)

```json
"OCR content not found"
```

---

### 1.3. Get QA Content

Lấy chỉ nội dung Q&A của document.

```
GET /api/v1/documents/{id:guid}/content/qa
```

### Response (200)

```json
[
  {
    "chunk_infor": {
      "type": "Text",
      "tokens_count": 512,
      "title": "1. Mục tiêu",
      "tittle_hirarchy": "Báo cáo / 1. Mục tiêu",
      "content": "Nội dung text của chunk...",
      "content_summary": null,
      "table_chunks": []
    },
    "qas": [
      {
        "question": "Câu hỏi gì đó?",
        "answer": "Câu trả lời chi tiết nhưng súc tích, trích xuất từ tài liệu",
        "category": "Objective"
      }
    ]
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| chunk_infor | ChunkInfo | Thông tin chunk |
| chunk_infor.type | TypeChunk | Loại chunk: Text, Table, Summary |
| chunk_infor.tokens_count | int | Số tokens |
| chunk_infor.title | string? | Tiêu đề chunk |
| chunk_infor.tittle_hirarchy | string? | Hierarchical title path |
| chunk_infor.content | string | Nội dung text của chunk |
| chunk_infor.content_summary | string? | Content summary (chỉ khi type=Summary) |
| chunk_infor.table_chunks | ChunkInfo[] | Sub-chunks cho table |
| qas | ChunkQA[] | Danh sách cặp Q&A |
| qas[].question | string | Câu hỏi |
| qas[].answer | string | Câu trả lời |
| qas[].category | string? | Thể loại câu hỏi |

### Response (404)

```json
"QA content not found"
```

---

### 1.4. Get Summary Content

Lấy chỉ nội dung tóm tắt của document.

```
GET /api/v1/documents/{id:guid}/content/summary
```

### Response (200)

Trả về chuỗi text thuần túy.

```
Đây là báo cáo tổng hợp các hoạt động kinh doanh năm 2025...
```

### Response (404)

```json
"Summary content not found"
```

---

### 1.5. Get Processing Logs

Lấy lịch sử các thông báo xử lý của document **sau khi job đã hoàn tất**. Không dùng cho real-time — để nhận thông báo real-time, dùng endpoint SSE `/notifications`.

| Nguồn dữ liệu | Mô tả |
|---------------|-------|
| Database (`LogMessage.LogsOcr` / `LogsGenQa`) | Ghi vào cuối mỗi lần xử lý hoàn tất |

```
GET /api/v1/documents/{id:guid}/logs?type=ocr
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| type | string | ocr | Loại process: `ocr` hoặc `gen-qa` |

### Response (200)

```json
[
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "timestamp": "01/06/2025 10:30:48",
    "message": "OCR processing started",
    "status": "Processing",
    "processType": "ocr",
    "processingTime": null,
    "stage": "OCR"
  },
  {
    "documentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "timestamp": "01/06/2025 10:32:48",
    "message": "OCR processing succeeded",
    "status": "Succeeded",
    "processType": "ocr",
    "processingTime": 120,
    "stage": "OCR"
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| documentId | guid | ID document |
| timestamp | string | Thời gian (dd/MM/yyyy HH:mm:ss) |
| message | string | Nội dung thông báo |
| status | string | Trạng thái: `Pendding`, `Processing`, `Succeeded`, `Failed`, `Canceled` |
| processType | string? | Loại process: `ocr` hoặc `gen-qa` |
| processingTime | double? | Thời gian xử lý (giây) |
| stage | string | Giai đoạn: `OCR` hoặc `GenQA` |

### Response (400)

```json
"Invalid process type"
```

---

### 1.6. Subscribe Real-time Notifications (SSE)

Đăng ký nhận thông báo real-time qua Server-Sent Events.

```
GET /api/v1/documents/{id:guid}/notifications?type=ocr
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| type | string | ocr | Loại process: `ocr` hoặc `gen-qa` |

### Behavior

- Endpoint trả về stream `text/event-stream`
- Mỗi thông báo được gửi dưới dạng `NotificationMessage` JSON
- Client nên duy trì kết nối và xử lý các message khi chúng đến
- Kết nối sẽ đóng khi document hoàn tất xử lý hoặc có lỗi

### SSE Data Format

Mỗi sự kiện SSE gửi về có định dạng:

```
data: {"documentId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","timestamp":"01/06/2025 10:30:48","message":"OCR processing started","status":"Processing","processType":"ocr","processingTime":null,"stage":"OCR","entryId":"..."}
```

### NotificationMessage Fields

| Field | Type | Description | Ví dụ |
|-------|------|-------------|-------|
| documentId | guid | ID của document đang xử lý | `"3fa85f64-5717-4562-b3fc-2c963f66afa6"` |
| timestamp | string | Thời gian sự kiện (dd/MM/yyyy HH:mm:ss) | `"01/06/2025 10:30:48"` |
| message | string | Nội dung thông báo mô tả trạng thái | `"OCR processing started"` |
| status | string | Trạng thái xử lý hiện tại | `"Processing"`, `"Succeeded"`, `"Failed"` |
| processType | string? | Loại process: `ocr` hoặc `gen-qa` | `"ocr"` |
| processingTime | double? | Thời gian xử lý tính bằng giây (null nếu chưa xong) | `120`, `null` |
| stage | string | Giai đoạn xử lý: `OCR` hoặc `GenQA` | `"OCR"` |
| entryId | string? | ID của stream entry (dùng cho resume, có thể null) | `"entry-123"` |

### Status Values

| Status | Ý nghĩa |
|--------|---------|
| `Processing` | Đang xử lý |
| `Succeeded` | Xử lý thành công |
| `Failed` | Xử lý thất bại |
| `Canceled` | Bị hủy |

## Part 2: OCR Operations

Base URL: `/api/v1/documents`

---

### 2.1. Trigger OCR Processing

Kích hoạt OCR processing cho document (sử dụng file đã upload lên S3).

```
POST /api/v1/documents/ocr/process/{id:guid}?modelId=chandraocr
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| modelId | string? | chandraocr | OCR model ID |

### Response (200)

```json
{
  "taskId": "ext-task-xyz-123",
  "message": "OCR processing started"
}
```

### Response (400)

```json
"Error message"
```

### Response (404)

```json
"File not found"
```

### Response (500) - OCR Server Unavailable

```json
"OCR server timeout after 3 seconds"
```

### Behavior

- **Health check trước khi xử lý**: API gọi `GET /health` đến OCR server trước mỗi lần process. Nếu server không phản hồi trong 3 giây hoặc trả về lỗi, API sẽ trả về HTTP 500 với message `"OCR server timeout after 3 seconds"`.
- Document phải có `status = Uploaded` hoặc `status = Failed`
- Upload file đã được thực hiện qua Dataset API
- OCR job được tạo, document status chuyển sang `ProcessingOcr`
- Kết quả OCR được lưu vào `Document.OcrContent`

---

### 2.2. Cancel OCR Job

Hủy OCR job đang chạy.

```
POST /api/v1/documents/ocr/cancel/{id:guid}
```

### Response (200)

```json
{
  "message": "OCR job cancelled successfully"
}
```

### Response (400)

```json
"OCR job not found or cannot be canceled"
```

### Response (404)

```json
"File not found"
```

---

## Part 3: GenQA Operations

Base URL: `/api/v1/documents`

---

### 3.1. Trigger QA Generation

Kích hoạt tạo Q&A cho document đã OCR.

```
POST /api/v1/documents/gen-qa/process/{id:guid}
```

### Prerequisites

- Document phải có `isOcred = true` (đã OCR xong)
- Document phải có `status = Successed` (không phải đang xử lý)

### Response (202 Accepted)

```json
{
  "message": "QA generation job scheduled"
}
```

### Response (400)

```json
"Document must be OCRed first"
```

### Response (404)

```json
"File not found"
```

### Behavior

- GenQA job được tạo trong Hangfire
- Document status chuyển sang `ProcessingGenQa`
- Kết quả Q&A được lưu vào `Document.QaContent` (JSON array)

---

### 3.2. Cancel GenQA Job

Hủy GenQA job đang chạy.

```
POST /api/v1/documents/gen-qa/cancel/{id:guid}
```

### Response (200)

```json
{
  "message": "GenQA job cancelled successfully"
}
```

### Response (400)

```json
"GenQA job not found or cannot be canceled"
```

### Response (404)

```json
"File not found"
```

---

## Part 4: Download Operations

Base URL: `/api/v1/documents`

---

### 4.1. Download Document

Tải xuống file gốc hoặc nội dung đã xử lý.

```
GET /api/v1/documents/{id:guid}/download?scope=original
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| scope | string | original | Phạm vi download: `original`, `qa-markdown`, `qa-json` |

### Scope Values

| Scope | Content-Type | Description |
|-------|-------------|-------------|
| `original` | application/pdf | File gốc đã upload |
| `qa-markdown` | text/markdown | Q&A content xuất ra dạng Markdown |
| `qa-json` | application/json | Q&A content xuất ra dạng JSON |

### Response (200)

File stream với Content-Type và Content-Disposition header phù hợp.

### Response (400)

```json
"Invalid scope"
```

### Response (404)

```json
"File not found"
```

## Common Error Responses

| Status | Description |
|--------|-------------|
| 401 | Unauthorized — Chưa đăng nhập |
| 404 | Not Found — Document hoặc Job không tồn tại |
| 400 | Bad Request — Validation lỗi hoặc điều kiện không đáp ứng |
| 500 | Internal Server Error — Lỗi hệ thống hoặc OCR server unavailable (timeout 3s) |

---

## Document Status Values

| Status | Ý nghĩa |
|--------|---------|
| `Uploaded` | File vừa upload, chưa xử lý gì |
| `ProcessingOcr` | Đang chạy OCR |
| `Successed` | OCR hoàn tất, đã gen Q&A thành công |
| `Failed` | Xử lý thất bại (OCR hoặc GenQA lỗi) |
| `ProcessingGenQa` | Đang gen Q&A |
| `Canceled` | User hủy tiến trình xử lý |

---

## Processing Log Status Values

| Status | Ý nghĩa |
|--------|---------|
| `Pendding` | Đang chờ |
| `Processing` | Đang xử lý |
| `Succeeded` | Thành công |
| `Failed` | Thất bại |
| `Canceled` | Bị hủy |
