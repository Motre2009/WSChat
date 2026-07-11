namespace WSChat.Infrastructure.Reports;

public class ReportRequest
{
    public string Period { get; set; } = "last2days";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string Format { get; set; } = "pdf";
    public string Title { get; set; } = "Chat Statistics";
    public string Language { get; set; } = "en";
}

