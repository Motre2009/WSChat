using System.Reflection.Metadata;

namespace WSChat.Shared;

public class User 
{
    public string Username { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; }
    public DateTime CreatedAt { get; set; }
}
