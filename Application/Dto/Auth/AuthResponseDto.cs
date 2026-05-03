namespace MarkdownGenQAs.Application.Dto.Auth;

public class AuthResponseDto
{
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public string? Message { get; set; }
    public List<string> Roles { get; set; } = [];
}
