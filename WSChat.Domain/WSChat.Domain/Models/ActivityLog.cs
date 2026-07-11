using System;

namespace WSChat.Domain.Models;

public class ActivityLog
{
    public int Id { get; set; }
    public string Action { get; set; } = null!;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = null!;
    public string? Message { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string? Details { get; set; }
}