namespace WSChat.Shared;

public class ChatMessage
{
    public string User { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsMine { get; set; }
}
