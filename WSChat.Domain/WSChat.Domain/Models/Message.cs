using System;

namespace WSChat.Domain.Models;

public class Message
{
    public long Id { get; set; }
    public string Text { get; set; } = null!;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public bool IsDeleted { get; set; } = false;
}