using Microsoft.EntityFrameworkCore;
using WSChat.Domain.Models;
using WSChat.Infrastructure.Data;

namespace WSChat.Application.Services;

public class AuthService
{
    public readonly ChatDbContext _db;

    public AuthService(ChatDbContext db)
    {
        _db = db;
    }

    public async Task<User?> RegisterAsync(string username, string email, string password)
    {
        if (await _db.Users.AnyAsync(u => u.Username == username || u.Email == email))
            return null;

        if (await _db.Users.AnyAsync(u => u.Username == username && u.IsDeleted))
            return null;

        var userRole = await _db.Roles.FirstAsync(r => r.Name == "User");

        var user = new User
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            RoleId = userRole.Id
        });

        await _db.SaveChangesAsync();

        return user;
    }

    public async Task<User?> LoginAsync(string username, string password)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return null;

        if (user.IsDeleted) return null;

        var activeBan = await _db.Bans
            .Where(b => b.UserId == user.Id && (b.BannedUntil == null || b.BannedUntil > DateTime.UtcNow))
            .FirstOrDefaultAsync();

        if (activeBan != null)
            return null;

        user.IsOnline = true;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<string> GenerateRememberTokenAsync(int userId)
    {
        var token = Guid.NewGuid().ToString();
        var user = await _db.Users.FindAsync(userId);
        if (user != null)
        {
            user.RememberToken = token;
            user.RememberTokenExpires = DateTime.UtcNow.AddDays(30);
            await _db.SaveChangesAsync();
        }
        return token;
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        return await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Username == username);
    }

    public async Task<User?> GetUserByRememberTokenAsync(string token)
    {
        return await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u =>
                u.RememberToken == token &&
                u.RememberTokenExpires > DateTime.UtcNow &&
                !u.IsDeleted);
    }

    public async Task<User?> LoginWithTokenAsync(string token)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u =>
                u.RememberToken == token &&
                u.RememberTokenExpires > DateTime.UtcNow &&
                !u.IsDeleted);

        if (user == null) return null;

        var activeBan = await _db.Bans
            .Where(b => b.UserId == user.Id && (b.BannedUntil == null || b.BannedUntil > DateTime.UtcNow))
            .FirstOrDefaultAsync();

        if (activeBan != null) return null;

        user.IsOnline = true;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<bool> SendPasswordResetEmailAsync(string emailOrUsername, EmailService emailService)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u =>
                (u.Email == emailOrUsername || u.Username == emailOrUsername) &&
                !u.IsDeleted);

        if (user == null) return false;

        var resetToken = Guid.NewGuid().ToString("N");
        user.PasswordResetToken = resetToken;
        user.PasswordResetExpires = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        return await emailService.SendPasswordResetEmailAsync(user.Email, user.Username, resetToken);
    }

    public async Task<bool> ResetPasswordAsync(string token, string newPassword)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u =>
                u.PasswordResetToken == token &&
                u.PasswordResetExpires > DateTime.UtcNow &&
                !u.IsDeleted);

        if (user == null) return false;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetExpires = null;
        await _db.SaveChangesAsync();

        return true;
    }

    public async Task<BanResult> BanUserAsync(string targetUsername, string bannedByUsername, int minutes, string reason = "Banned by admin")
    {
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Username == targetUsername);
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Username == bannedByUsername);

        if (target == null) return BanResult.UserNotFound;
        if (admin == null) return BanResult.AdminNotFound;

        var ban = new Ban
        {
            UserId = target.Id,
            BannedById = admin.Id,
            Reason = reason,
            BannedUntil = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null
        };

        _db.Bans.Add(ban);
        await _db.SaveChangesAsync();
        return BanResult.Success;
    }

    public async Task<bool> DeleteUserAsync(string targetUsername)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == targetUsername);
        if (user == null) return false;

        user.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public enum BanResult { Success, UserNotFound, AdminNotFound }
}
