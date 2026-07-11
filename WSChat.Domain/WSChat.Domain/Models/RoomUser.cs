using System;

namespace WSChat.Domain.Models;

public class RoomUser
{
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}