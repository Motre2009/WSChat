using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace WSChat.Domain.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = null!;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = null!;
    public bool IsOnline { get; set; } = false;
    public string? RememberToken { get; set; }
    public DateTime? RememberTokenExpires { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetExpires { get; set; }
    public bool IsDeleted { get; set; } = false;
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Ban> Bans { get; set; } = new List<Ban>();
    public ICollection<Ban> BansIssued { get; set; } = new List<Ban>();
}