namespace WSChat.Shared;

public class ChatMessage
{
    public string? User { get; set; }
    public string? Text { get; set; }
    public bool IsMine { get; set; }
    public DateTime? Timestamp { get; set; }

    public string FormattedTime
    {
        get
        {
            if (!Timestamp.HasValue) return string.Empty;

            var localTime = Timestamp.Value.ToLocalTime();

            return localTime.ToString("HH:mm");
        }
    }
}
