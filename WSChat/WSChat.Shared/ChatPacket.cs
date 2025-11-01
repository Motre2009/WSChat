namespace WSChat.Shared;

public class ChatPacket
{
    public string Type { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;  
    public string To { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
