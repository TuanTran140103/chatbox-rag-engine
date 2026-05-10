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
| qas | ChunkQA[] | Danh sách cặp câu hỏi-trả lời. **v2+**: QA của table được gộp vào đây với `qa_type: "table"`. |
| qas[].question | string | Câu hỏi |
| qas[].answer | string | Câu trả lời (trích xuất từ document) |
| qas[].category | string? | Thể loại câu hỏi |
| qas[].qa_type | string? | **v2+** Loại QA: `"text"` — từ văn bản; `"table"` — từ bảng biểu |
| table_chunk_qas | ChunkQAInfor[]? | **Deprecated (v2/v3)** — Giữ lại để tương thích ngược |

### ChunkQAInfor — v2/v3 Structure (combined QA)

Từ v2 trở đi, `table_chunk_qas` bị xoá. QA của table được gộp chung vào mảng `qas` với field `qa_type` phân biệt:

```json
{
  "chunk_infor": { "type": "Text", ... },
  "qas": [
    { "question": "...", "answer": "...", "category": "...", "qa_type": "text" },
    { "question": "...", "answer": "...", "category": "...", "qa_type": "table" }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| qas[].qa_type | string | `"text"` — QA từ văn bản; `"table"` — QA từ bảng biểu |

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
        "category": "Objective",
        "qa_type": "text"
      },
      {
        "question": "Chỉ tiêu doanh thu năm 2025 là bao nhiêu?",
        "answer": "100 tỷ",
        "category": "Financial",
        "qa_type": "table"
      }
    ],
    "table_chunk_qas": null
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
| qas[].qa_type | string? | Loại QA: `"text"` — từ văn bản; `"table"` — từ bảng biểu trong chunk. **Chỉ có khi dùng v2 API**. |
| table_chunk_qas | ChunkQAInfor[]? | **Deprecated (v2/v3)** — Q&A của table được gộp vào `qas` với `qa_type: "table"`. Giữ lại để tương thích ngược. |

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
| status | string | Trạng thái: `Pending`, `Processing`, `Succeeded`, `Failed`, `Canceled` |
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
| status | string | Trạng thái xử lý hiện tại | `"Pending"`, `"Processing"`, `"Succeeded"`, `"Failed"`, `"Canceled"` |
| processType | string? | Loại process: `ocr` hoặc `gen-qa` | `"ocr"` |
| processingTime | double? | Thời gian xử lý tính bằng giây (null nếu chưa xong) | `120`, `null` |
| stage | string | Giai đoạn xử lý: `OCR` hoặc `GenQA` | `"OCR"` |
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
- `document.Status` chuyển sang `ProcessingGenQa`
- `DocumentJob.StatusGenQa` được set là `Pending` — chờ Hangfire schedule job chạy
- Khi job bắt đầu thực thi, `StatusGenQa` chuyển thành `Processing`
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

Ghi đè metadata sau khi human review. Dùng khi AI extract sai hoặc muốn sửa tay.

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
- Không trigger lại GenQA hay OCR
- Dùng cho human review override kết quả AI extraction

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
| scope | string | original | Phạm vi download: `original`, `ocr-markdown`, `qa-markdown`, `all` |

### Scope Values

| Scope | Content-Type | Description |
|-------|-------------|-------------|
| `original` | application/pdf | File gốc đã upload (lấy từ S3) |
| `ocr-markdown` | text/markdown | OCR content dạng Markdown |
| `qa-markdown` | text/markdown | Q&A content xuất ra dạng Markdown |
| `all` | application/zip | ZIP chứa tất cả: original + OCR + Q&A + Summary |

### `all` scope — nội dung trong ZIP

| File trong ZIP | Điều kiện |
|----------------|-----------|
| `{filename}.pdf` (hoặc .docx) | Nếu file gốc tồn tại |
| `{filename}.md` | Nếu đã OCR |
| `{filename}_QAs.md` | Nếu đã gen Q&A |
| `{filename}_Summary.md` | Nếu có summary |

### Response (200)

File stream với Content-Type và Content-Disposition header phù hợp.

### Response (400)

```json
"Invalid scope. Allowed values: original, ocr-markdown, qa-markdown, all"
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
| `Successed` | OCR hoàn tất, đã gen Q&A thành công |
| `Failed` | Xử lý thất bại (OCR hoặc GenQA lỗi) |
| `ProcessingGenQa` | Đang gen Q&A (hoặc đang chờ Hangfire schedule chạy) |
| `Canceled` | User hủy tiến trình xử lý |

---

## Job Status Values (`StatusJob`)

`DocumentJob` có trường `StatusOcr` và `StatusGenQa` với các giá trị:

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

**Ví dụ luồng trạng thái cho GenQA:**
```
API call → StatusGenQa = Pending (chờ Hangfire run)
Job chạy → StatusGenQa = Processing (đang gen QA thực tế)
Xong     → StatusGenQa = Succeeded
```
