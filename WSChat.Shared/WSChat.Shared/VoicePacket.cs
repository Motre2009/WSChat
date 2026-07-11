namespace WSChat.Shared;

public class VoicePacket
{
    public string? From { get; set; }
    public string? To { get; set; }
    public byte[]? Data { get; set; }
}
