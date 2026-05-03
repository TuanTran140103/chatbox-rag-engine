namespace MarkdownGenQAs.Options;

public class InitialSettings
{
    public const string SectionName = "InitialSettings";
    public AdminUserConfig AdminUser { get; set; } = new();
}

public class AdminUserConfig
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string UserName { get; set; } = "admin";
}
