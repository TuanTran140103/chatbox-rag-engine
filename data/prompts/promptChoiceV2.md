Nhiệm vụ:
Phân tích đoạn văn bản chứa hai bảng (Bảng 1 và Bảng 2) được trích xuất qua OCR. Xác định xem Bảng 2 có phải là phần tiếp nối trực tiếp về mặt nội dung hoặc logic của Bảng 1 hay không.

--------------------------------
Định nghĩa về sự tiếp nối (Continuation)
--------------------------------
Bảng 2 được coi là tiếp nối của Bảng 1 nếu BẤT KỲ điều nào sau đây là đúng:

1. Nội dung bên trong bất kỳ ô nào của Bảng 1 chưa kết thúc và được viết tiếp ở Bảng 2.
2. Một câu, đoạn văn, danh sách số hoặc danh sách dấu chấm bắt đầu trong Bảng 1 và tiếp tục trong Bảng 2.
3. Bảng 2 tiếp tục nội dung trong CÙNG MỘT HÀNG hoặc CÙNG MỘT Ô như Bảng 1 (ngay cả khi các ô đầu bị trống).
4. Bảng 2 lược bỏ tiêu đề (header) nhưng rõ ràng đi theo các cột giống Bảng 1.
5. **LIÊN KẾT LOGIC (Quan trọng):** Bảng 2 là một phần không thể tách rời của quy trình đang mô tả ở Bảng 1. 
   - Ví dụ: Bảng 1 liệt kê các "Bước thực hiện", và Bảng 2 là bảng "Nếu/Thì" (để xử lý các tình huống phát sinh của bước đó).
   - Ví dụ: Bảng 2 cung cấp các ghi chú, mã lỗi hoặc kết quả tương ứng cho các dữ liệu ở Bảng 1.

--------------------------------
Quy tắc về Tiêu đề (Header rules)
--------------------------------

Trả lời **Yes** nếu:
- Tiêu đề Bảng 2 lặp lại chính xác tiêu đề Bảng 1 do ngắt trang.
- Bảng 2 không có tiêu đề (đoạn mã html không có thẻ thead) và nội dung khớp với luồng của Bảng 1.
- Tiêu đề Bảng 2 thay đổi nhưng mang tính chất bổ trợ logic (ví dụ: "Nếu/Thì", "Kết quả", "Ghi chú") cho dữ liệu ở bảng trước.

Trả lời **No** nếu:
1. Bảng 2 bắt đầu một chủ đề hoàn toàn mới, không liên quan đến quy trình của Bảng 1.
2. Bảng 2 là một bảng định nghĩa dữ liệu (Data Dictionary) hoặc Metadata không liên quan đến dữ liệu đang trình bày.
3. Có tiêu đề chương hoặc mục lớn mới ngăn cách giữa hai bảng làm thay đổi hoàn toàn ngữ cảnh.

--------------------------------
Dấu hiệu không tiếp nối
--------------------------------
Luôn trả lời **No** nếu Bảng 2 là:
- Khối chữ ký/phê duyệt (Chữ ký, Họ và tên, Chức vụ…)
- Bảng kiểm soát tài liệu.
- Các mục lục hoặc bảng danh mục không liên quan.

--------------------------------
Định dạng Input
--------------------------------
Dữ liệu được cung cấp dưới dạng:
- **Ngữ cảnh toàn bộ ({0}):** Bao gồm Bảng 1 + Văn bản nằm giữa + Bảng 2. Bạn cần nhìn vào toàn bộ luồng văn bản này để thấy sự liên kết.
- **Bảng 2 mục tiêu ({1}):** Chỉ chứa nội dung của Bảng 2 để bạn xác định rõ đối tượng cần kiểm tra.

--------------------------------
Input
--------------------------------
--- Ngữ cảnh toàn bộ ---
{0}

--- Bảng 2 cần kiểm tra ---
{1}

--------------------------------
Ràng buộc đầu ra
--------------------------------
Chỉ trả lời bằng đúng MỘT từ: **Yes** hoặc **No**
Không giải thích. Không có dấu câu. Không thêm văn bản thừa.
