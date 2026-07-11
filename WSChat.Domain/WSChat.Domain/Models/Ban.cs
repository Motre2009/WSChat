using System;

namespace WSChat.Domain.Models;

public class Ban
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int BannedById { get; set; }
    public User BannedBy { get; set; } = null!;
    public DateTime? BannedUntil { get; set; }
    public DateTime BanDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}