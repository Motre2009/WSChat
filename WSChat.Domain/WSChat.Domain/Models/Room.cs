using System.Collections.Generic;

namespace WSChat.Domain.Models;

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public int CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RoomUser> RoomUsers { get; set; } = new List<RoomUser>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
