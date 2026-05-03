namespace GenQAServer.Options;
public class HangfireOptions
{
    public const string SectionName = "Hangfire";
    public string RedisConnection { get; set; } = "localhost:6379,abortConnect=false";
    public string DashboardPath { get; set; } = "/hangfire";
    public string DashboardTitle { get; set; } = "Background Jobs";
    public int WorkerCount { get; set; } = 0; // 0 = auto (ProcessorCount * 2)
}
