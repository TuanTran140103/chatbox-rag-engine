# Database Schema - MarkdownGenQAs

Tài liệu này mô tả cấu trúc cơ sở dữ liệu và luồng hoạt động của hệ thống MarkdownGenQAs. Hệ thống sử dụng **PostgreSQL** làm DB chính với các tính năng nâng cao như tìm kiếm Full-text (via `pg_trgm`) và lưu trữ dữ liệu bán cấu trúc (via `jsonb`).

## 1. Hệ thống Quản lý Người dùng & Đơn vị (Identity & Org)

Hệ thống dựa trên ASP.NET Core Identity và mở rộng thêm quản lý đơn vị tổ chức.

- **Users (`Users`)**: Thông tin định danh. Có index `trgm` trên Email để tìm kiếm nhanh.
- **Roles (`Roles`)**: Các vai trò hệ thống.
- **OrganizationUnits (`OrganizationUnits`)**: Quản lý cây thư mục tổ chức (phòng ban/đơn vị). Sử dụng cột `Path` để truy vấn nhanh các đơn vị con trong nhánh.
- **UserPositions (`UserPositions`)**: Gán người dùng vào các đơn vị tổ chức với vai trò cụ thể trong đơn vị đó. Một người có thể kiêm nhiệm nhiều vị trí.

## 2. Quản lý Nội dung (Datasets & Documents)

Đây là trung tâm dữ liệu của hệ thống.

- **Datasets (`Datasets`)**: Tập hợp các tài liệu. Thuộc sở hữu của một cá nhân (`OwnerUserId`) hoặc một đơn vị (`OUId`).
- **DatasetItems (`DatasetItems`)**: Đại diện cho cấu trúc file/folder trong Dataset. 
    - Nếu là folder, nó có thể chứa các Item con (`ParentId`).
    - Nếu là file, nó liên kết 1-1 với bản ghi `Document`.
- **Documents (`Documents`)**: Lưu trữ metadata của file PDF và kết quả xử lý.
    - `ObjectKeyFilePdf`: Đường dẫn file trên S3/Minio.
    - `OcrContent`, `QaContent`, `SummaryContent`: Kết quả từ AI.

## 3. Quy trình Xử lý & Log (Processing & Logging)

- **DocumentJobs (`DocumentJobs`)**: Theo dõi trạng thái của các tiến trình xử lý ngầm (Background Jobs). 
    - Có trạng thái riêng cho OCR và GenQA.
    - Ràng buộc: Chỉ GenQA khi OCR đã thành công.
- **LogMessages (`LogMessages`)**: Lưu trữ vết xử lý chi tiết từ các worker. Sử dụng kiểu dữ liệu `jsonb` để lưu danh sách các sự kiện (`LogEvent`) một cách linh hoạt.
- **SystemStatistics (`SystemStatistics`)**: Lưu trữ các con số thống kê (tổng số tài liệu, dung lượng lưu trữ) theo từng đơn vị tổ chức để phục vụ dashboard.

## 4. Kiểm soát Truy cập (Access Control)

- **AccessShares (`AccessShares`)**: Quản lý chia sẻ quyền.
    - Có thể chia sẻ toàn bộ Dataset hoặc chỉ một phần (`DatasetItemId`).
    - Đối tượng được chia sẻ: Người dùng cụ thể hoặc toàn bộ đơn vị.
    - Cơ chế **Soft Delete** (`IsDeleted`): Được áp dụng để giữ lại lịch sử quyền truy cập (Audit Trail), cho phép khôi phục nhanh và đảm bảo tính toàn vẹn cho các log hệ thống.

---

# Luồng hoạt động hệ thống (System Workflow)

1.  **Ingestion**: Người dùng tải PDF lên -> Lưu vào S3 -> Tạo `Document` & `DatasetItem`.
2.  **OCR Phase**: Worker quét file PDF trích xuất text -> Cập nhật `OcrContent` -> Đánh dấu `IsOcred`.
3.  **AI Phase (GenQA)**: Dựa trên `OcrContent`, LLM thực hiện sinh bộ câu hỏi/trả lời và tóm tắt -> Cập nhật `QaContent`, `SummaryContent` -> Đánh dấu `IsQaGenerated`.
4.  **Security**: Chủ sở hữu thiết lập `AccessShare` để người khác có thể khai thác dữ liệu.
5.  **Audit**: Mọi thay đổi về dữ liệu và quyền truy cập đều được ghi vết qua `IAuditUser` và `IAuditDelete`.
