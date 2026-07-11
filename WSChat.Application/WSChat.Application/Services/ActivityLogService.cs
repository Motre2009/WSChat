using WSChat.Domain.Models;
using WSChat.Infrastructure.Data;

namespace WSChat.Application.Services;

public class ActivityLogService
{
    private readonly ChatDbContext _dbContext;

    public ActivityLogService(ChatDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task LogActivityAsync(int userId, string eventType, string action, string? details = null)
    {
        var log = new ActivityLog
        {
            UserId = userId,
            EventType = eventType,
            Action = action,
            Message = details ?? $"{action} by user {userId}",
            Timestamp = DateTime.UtcNow
        };

        _dbContext.ActivityLogs.Add(log);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ActivityLog] Failed to save log: {ex.Message}");
        }
    }
}