using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MarkdownGenQAs.Models.QA;

public class QA
{
    [Description("Câu hỏi cho tài liệu")]
    [Required]
    [JsonPropertyName("question")]
    public required string Question { get; set; }

    [Description("Câu trả lời chi tiết nhưng súc tích, trích xuất từ tài liệu")]
    [Required]
    [JsonPropertyName("answer")]
    public required string Answer { get; set; }
}

public enum SummaryCategory
{
    [Description("Mục tiêu chính của tài liệu")]
    Objective,

    [Description("Đối tượng độc giả mục tiêu")]
    Audience,

    [Description("Các chủ đề hoặc chương mục chính")]
    KeyTopics,

    [Description("Những điểm rút ra quan trọng nhất")]
    Takeaways,

    [Description("Phạm vi và giới hạn của tài liệu")]
    Scope
}

public enum TableQACategory
{
    [Description("Mục tiêu, mục đích chính của yêu cầu hoặc section tài liệu")]
    Objective,

    [Description("Phạm vi áp dụng và giới hạn của yêu cầu hoặc section tài liệu")]
    Scope,

    [Description("Định nghĩa khái niệm, thuật ngữ hoặc viết tắt được sử dụng trong tài liệu")]
    Definition,

    [Description("Quy tắc nghiệp vụ bắt buộc phải tuân thủ")]
    Rule,

    [Description("Các ràng buộc pháp lý, kỹ thuật, hệ thống hoặc nghiệp vụ")]
    Constraint,

    [Description("Quy trình hoặc luồng nghiệp vụ gồm các bước thực hiện cụ thể")]
    Process,

    [Description("Thông tin dữ liệu, các trường dữ liệu hoặc cấu trúc dữ liệu liên quan")]
    Data,

    [Description("Trường hợp ngoại lệ, tình huống đặc biệt hoặc xử lý lỗi")]
    Exception,

    [Description("Lịch sử chỉnh sửa tài liệu, thay đổi phiên bản, ngày cập nhật và mô tả thay đổi")]
    ChangeHistory,

    [Description("Các bên liên quan như người biên soạn, kiểm tra, phê duyệt hoặc đơn vị chịu trách nhiệm")]
    Stakeholder,

    [Description("Thông tin liên quan đến phê duyệt, ký duyệt, xác nhận nội dung tài liệu")]
    Approval,

    [Description("Thông tin phiên bản của tài liệu, ngày ban hành hoặc ký hiệu version")]
    Version,

    [Description("Danh mục tài liệu tham khảo, văn bản viện dẫn, tiêu chuẩn hoặc quy định liên quan được sử dụng làm căn cứ xây dựng tài liệu hiện tại")]
    Reference,

    [Description("Nội dung khác quan trọng về nghiệp vụ nhưng không thuộc các nhóm trên")]
    Other
}

public enum TextQACategory
{
    [Description("Mục tiêu, mục đích chính của yêu cầu hoặc section tài liệu")]
    Objective,

    [Description("Phạm vi áp dụng và giới hạn của yêu cầu hoặc section tài liệu")]
    Scope,

    [Description("Định nghĩa khái niệm, thuật ngữ hoặc viết tắt được sử dụng trong tài liệu")]
    Definition,

    [Description("Quy tắc nghiệp vụ bắt buộc phải tuân thủ")]
    Rule,

    [Description("Các ràng buộc pháp lý, kỹ thuật, hệ thống hoặc nghiệp vụ")]
    Constraint,

    [Description("Quy trình hoặc luồng nghiệp vụ gồm các bước thực hiện cụ thể")]
    Process,

    [Description("Thông tin dữ liệu, các trường dữ liệu hoặc cấu trúc dữ liệu liên quan")]
    Data,

    [Description("Trường hợp ngoại lệ, tình huống đặc biệt hoặc xử lý lỗi")]
    Exception,

    [Description("Lịch sử chỉnh sửa tài liệu, thay đổi phiên bản, ngày cập nhật và mô tả thay đổi")]
    Change_History,

    [Description("Các bên liên quan như người biên soạn, kiểm tra, phê duyệt hoặc đơn vị chịu trách nhiệm")]
    Stakeholder,

    [Description("Thông tin liên quan đến phê duyệt, ký duyệt, xác nhận nội dung tài liệu")]
    Approval,

    [Description("Thông tin phiên bản của tài liệu, ngày ban hành hoặc ký hiệu version")]
    Version,

    [Description("Nội dung khác quan trọng về nghiệp vụ nhưng không thuộc các nhóm trên")]
    Other
}


public class SummaryQA : QA
{
    [Description("Phân loại mục đích của câu hỏi: Objective, Audience, KeyTopics, Takeaways, hoặc Scope")]
    [JsonPropertyName("category")]
    public SummaryCategory Category { get; set; }
}

public class TableQA : QA
{
    [Description("Thể loại câu hỏi")]
    [JsonPropertyName("category")]
    public TableQACategory category { get; set; }
}

public class TextQA : QA
{
    [Description("Thể loại câu hỏi")]
    [JsonPropertyName("category")]
    public TextQACategory Category { get; set; }
}