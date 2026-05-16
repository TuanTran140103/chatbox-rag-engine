# Document API

Base URL: `/api/v1/documents`

Tất cả các API trong nhóm này yêu cầu **đăng nhập** (`[Authorize]`), không yêu cầu Admin.

> **Lưu ý:** Upload document **không có endpoint riêng** — việc upload file đã được tích hợp trong Dataset API (`POST /api/v1/user/me/datasets/{id}/create-item` với `type=1`). Các endpoint bên dưới dùng để quản lý document đã upload.

---

## Part 1: Document Management

Base URL: `/api/v1/documents`

---

### 1.1. Get Document Detail

Lấy thông tin chi tiết document, bao gồm nội dung OCR, Summary, và Metadata.

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
  "isOcred": true,
  "isIndexed": false,
  "ocrCount": 10,
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa7",
  "categoryId": "3fa85f64-5717-4562-b3fc-2c963f66afa8",
  "categoryName": "Báo cáo",
  "createdAt": "2025-01-01T00:00:00Z",
  "content": {
    "ocrMarkdown": "# Bao cao 2025\n\n## Section 1...",
    "summary": null
  },
  "metadata": {
    "isMetadataExtracted": true,
    "metadataContent": "{\"field1\":\"value1\",\"field2\":\"value2\"}",
    "metadataError": null
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
| isOcred | bool | Đã OCR chưa |
| isIndexed | bool | Đã index lên Qdrant chưa |
| ocrCount | int | Số trang OCR |
| userId | guid? | User upload |
| categoryId | guid? | Category ID |
| categoryName | string? | Tên category |
| createdAt | datetime | Thời gian tạo |
| content | object | Nội dung chi tiết |

**Inner (DocumentContent)**

| Field | Type | Description |
|-------|------|-------------|
| ocrMarkdown | string? | Toàn bộ nội dung OCR dạng Markdown |
| summary | string? | Tóm tắt document (từ LLM) |

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

### 1.3. Get Chunk Content

Lấy nội dung chunks (JSON) của document — kết quả từ bước chunking trong Indexing Pipeline.

```
GET /api/v1/documents/{id:guid}/content/chunks
```

### Response (200)

Trả về JSON array của `ChunkInfo`:

```json
[
  {
    "type": "Text",
    "tokens_count": 512,
    "title": "1. Mục tiêu",
    "tittle_hirarchy": "Báo cáo / 1. Mục tiêu",
    "content": "Nội dung text của chunk...",
    "content_summary": null,
    "index": 0
  },
  {
    "type": "Table",
    "tokens_count": 128,
    "title": "Bảng doanh thu",
    "content": "| Năm | Doanh thu |\n|-----|----------|\n| 2024 | 80 tỷ |\n| 2025 | 100 tỷ |",
    "index": 5
  }
]
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| type | TypeChunk | Loại chunk: `Text`, `Table` |
| tokens_count | int | Số tokens của chunk |
| title | string? | Tiêu đề chunk (từ heading gần nhất) |
| tittle_hirarchy | string? | Hierarchical title path |
| content | string | Nội dung text của chunk |
| content_summary | string? | Content summary (cho chunk lớn cần tóm tắt) |
| index | int | Thứ tự chunk trong document |

### Response (404)

```json
"Chunk content not found"
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
| Database (`LogMessage.LogsOcr` / `LogsIndexing`) | Ghi vào cuối mỗi lần xử lý hoàn tất |

```
GET /api/v1/documents/{id:guid}/logs?type=ocr
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| type | string | ocr | Loại process: `ocr` hoặc `indexing` |

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
| status | string | Trạng thái: `Pending`, `Processing`, `Succeeded`, `Failed`, `Canceled` |
| processType | string? | Loại process: `ocr` hoặc `indexing` |
| processingTime | double? | Thời gian xử lý (giây) |
| stage | string | Giai đoạn: `OCR` hoặc `Indexing` |

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
| type | string | ocr | Loại process: `ocr` hoặc `indexing` |

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
| status | string | Trạng thái xử lý hiện tại | `"Pending"`, `"Processing"`, `"Succeeded"`, `"Failed"`, `"Canceled"` |
| processType | string? | Loại process: `ocr` hoặc `indexing` | `"ocr"` |
| processingTime | double? | Thời gian xử lý tính bằng giây (null nếu chưa xong) | `120`, `null` |
| stage | string | Giai đoạn xử lý: `OCR` hoặc `Indexing` | `"OCR"` |
| entryId | string? | ID của stream entry (dùng cho resume, có thể null) | `"entry-123"` |

### Status Values

| Status | Ý nghĩa |
|--------|---------|
| `Pending` | Đã nhận yêu cầu, chờ xử lý |
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
- OCR job được tạo, `document.Status` chuyển sang `ProcessingOcr`
- `DocumentJob.StatusOcr` được set là `Pending` — chờ OCR service bắt đầu
- Khi OCR service gửi tín hiệu "Started", `StatusOcr` chuyển thành `Processing`
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

## Part 3: Indexing Operations

Base URL: `/api/v1/documents`

---

### 3.1. Trigger Document Indexing

Kích hoạt Document Indexing Pipeline cho document đã OCR. Pipeline thực hiện đồng thời: **Metadata Extraction**, **Chunking**, **Summary**, sau đó **Index lên Qdrant**.

```
POST /api/v1/documents/indexing/process/{id:guid}
```

### Prerequisites

- Document phải có `isOcred = true` (đã OCR xong)
- Document phải có `status = Successed` (không phải đang xử lý)

### Response (202 Accepted)

```json
"documentId"
```

### Response (400)

```json
"OCR must be completed before indexing. Current status: ..."
```

### Response (404)

```json
"File record not found"
```

### Behavior

- Indexing job được tạo trong Hangfire qua `IDocumentIndexingBackgroundJobService`
- `document.Status` chuyển sang `ProcessingIndexing`
- `DocumentJob.StatusIndexing` được set là `Pending` — chờ Hangfire schedule

**Pipeline thực thi:**

```
PHASE 1 (Concurrent):
  ├── Metadata Extraction  → Document.MetadataContent
  ├── Chunking             → Document.ChunkContent (JSON: List<ChunkInfo>)
  │     ├── Text chunks    → MarkdownService.CreateChunkAsync
  │     ├── Table chunks   → MarkdownService.CreateChunkTableAsync
  │     └── Summarize large chunks (concurrency: 3)
  └── Summary              → Document.SummaryContent (GenQAsService)

PHASE 2 (Sau Phase 1):
  └── Index to Qdrant      → IQdrantService.AddDocumentPointAsync
        ├── Đọc chunks từ ChunkContent
        ├── Generate embeddings
        └── Upsert PointStructs lên collection "documents"

→ document.Status = Successed
→ document.IsIndexed = true
```

---

### 3.2. Cancel Indexing Job

Hủy indexing job đang chạy.

```
POST /api/v1/documents/indexing/cancel/{id:guid}
```

### Response (200)

```json
{
  "message": "Indexing job ... has been canceled."
}
```

### Response (400)

```json
"Indexing job is not running. Current status: ..."
```

### Response (404)

```json
"Document not found"
```

---

## Part 4: Metadata Operations

Base URL: `/api/v1/documents`

---

### 4.1. Get Document Metadata

Lấy kết quả metadata extraction của document.

```
GET /api/v1/documents/{id:guid}/metadata
```

### Response (200)

```json
{
  "isMetadataExtracted": true,
  "metadataContent": "{\"field1\":\"value1\",\"field2\":\"value2\"}",
  "metadataError": null
}
```

### Response Fields

| Field | Type | Description |
|-------|------|-------------|
| isMetadataExtracted | bool | Đã extract metadata chưa |
| metadataContent | string? | Nội dung metadata (JSON theo template schema của Dataset) |
| metadataError | string? | Lỗi extract metadata (nếu có) |

### Response (404)

```json
"File not found"
```

---

### 4.2. Update Document Metadata (Human Review)

Ghi đè metadata sau khi human review. Dùng khi AI extract sai hoặc muốn sửa tay. Sau khi update, có thể trigger lại Indexing Pipeline để cập nhật payload trên Qdrant.

```
PUT /api/v1/documents/{id:guid}/metadata
```

### Request Body

```json
{
  "metadataContent": "{\"field1\":\"corrected\",\"field2\":\"value2\"}",
  "isExtracted": true
}
```

### Request Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| metadataContent | string | Yes | Nội dung metadata JSON |
| isExtracted | bool | Yes | Đánh dấu metadata đã được duyệt/chấp nhận. Nếu `true`, set `IsMetadataExtracted = true` |

### Response (200)

```json
{
  "message": "Metadata updated successfully"
}
```

### Response (400)

```json
{
  "error": "Validation error message"
}
```

### Response (404)

```json
{
  "error": "Document not found"
}
```

### Behavior

- Cập nhật `Document.MetadataContent` với nội dung từ request body
- Chỉ set `Document.IsMetadataExtracted = true` nếu `isExtracted = true`
- Không trigger lại indexing hay OCR
- Dùng cho human review override kết quả AI extraction
- Để cập nhật metadata lên Qdrant, gọi `POST /indexing/process/{id}` sau khi update

---

## Part 5: Download Operations

Base URL: `/api/v1/documents`

---

### 5.1. Download Document

Tải xuống file gốc hoặc nội dung đã xử lý.

```
GET /api/v1/documents/{id:guid}/download?scope=original
```

### Query Parameters

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| scope | string | original | Phạm vi download: `original`, `ocr-markdown`, `chunks-markdown`, `all` |

### Scope Values

| Scope | Content-Type | Description |
|-------|-------------|-------------|
| `original` | application/pdf | File gốc đã upload (lấy từ S3) |
| `ocr-markdown` | text/markdown | OCR content dạng Markdown |
| `chunks-markdown` | application/json | Chunk content xuất ra dạng JSON |
| `all` | application/zip | ZIP chứa tất cả: original + OCR + Chunks + Summary |

### `all` scope — nội dung trong ZIP

| File trong ZIP | Điều kiện |
|----------------|-----------|
| `{filename}.pdf` (hoặc .docx) | Nếu file gốc tồn tại |
| `{filename}.md` | Nếu đã OCR |
| `{filename}_Chunks.json` | Nếu đã index (có ChunkContent) |
| `{filename}_Summary.md` | Nếu có summary |

### Response (200)

File stream với Content-Type và Content-Disposition header phù hợp.

### Response (400)

```json
"Invalid scope. Allowed values: original, ocr-markdown, chunks-markdown, all"
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

## Document Status Values (`StatusDocument`)

| Status | Ý nghĩa |
|--------|---------|
| `Uploaded` | File vừa upload, chưa xử lý gì |
| `ProcessingOcr` | Đang chạy OCR (hoặc đang chờ OCR service bắt đầu) |
| `Successed` | OCR hoàn tất, đã index thành công |
| `Failed` | Xử lý thất bại (OCR hoặc Indexing lỗi) |
| `ProcessingIndexing` | Đang chạy Indexing Pipeline (metadata + chunking + summary + Qdrant) |
| `Canceled` | User hủy tiến trình xử lý |

---

## Job Status Values (`StatusJob`)

`DocumentJob` có trường `StatusOcr` và `StatusIndexing` với các giá trị:

| Status | Ý nghĩa |
|--------|---------|
| `Pending` | Đã nhận yêu cầu, chờ bắt đầu |
| `Processing` | Đang xử lý thực tế |
| `Succeeded` | Thành công |
| `Failed` | Thất bại |
| `Canceled` | Bị hủy |

**Ví dụ luồng trạng thái cho OCR:**
```
API call → StatusOcr = Pending (chờ OCR service)
Started  → StatusOcr = Processing (đang OCR thực tế)
Xong     → StatusOcr = Succeeded
```

**Ví dụ luồng trạng thái cho Indexing:**
```
API call → StatusIndexing = Pending (chờ Hangfire run)
Job chạy → StatusIndexing = Processing (đang chạy Indexing Pipeline)
Xong     → StatusIndexing = Succeeded
```
