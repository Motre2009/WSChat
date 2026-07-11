using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using WSChat.Infrastructure.Data;
using WSChat.Domain.Models;

namespace WSChat.Infrastructure.Reports;

public class ReportService
{
    private readonly ChatDbContext _db;

    public ReportService(ChatDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateReportAsync(ReportRequest request)
    {
        DateTime fromDate;
        DateTime toDate = DateTime.UtcNow;

        if (request.Period == "last2days")
            fromDate = DateTime.UtcNow.AddDays(-2);
        else
        {
            fromDate = request.From ?? DateTime.UtcNow.AddDays(-2);
            toDate = request.To ?? DateTime.UtcNow;
        }

        var users = await _db.Users
            .OrderBy(u => u.Username)
            .ToListAsync();

        var activity = await _db.ActivityLogs
            .Where(a => a.Timestamp >= fromDate && a.Timestamp <= toDate)
            .GroupBy(a => a.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Count = g.Count(),
                LastActive = g.Max(a => a.Timestamp)
            })
            .ToListAsync();

        var messages = await _db.Messages
            .Where(m => m.SentAt >= fromDate && m.SentAt <= toDate)
            .OrderBy(m => m.SentAt)
            .Include(m => m.User)
            .Include(m => m.Room)
            .ToListAsync();

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(40);

                page.Header().Text(request.Title ?? "Chat Statistics").FontSize(24).Bold().AlignCenter();

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
                    col.Item().Text($"Period: {fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}");
                    col.Item().LineHorizontal(1);

                    col.Item().PaddingTop(20).Text("Users").FontSize(18).Bold();

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(30);
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("#").Bold();
                            header.Cell().Text("Username").Bold();
                            header.Cell().Text("Email").Bold();
                            header.Cell().Text("Role").Bold();
                            header.Cell().Text("Registered").Bold();
                        });

                        int index = 1;
                        foreach (var u in users)
                        {
                            string registered;
                            var createdAtProp = u.GetType().GetProperty("CreatedAt");
                            if (createdAtProp != null)
                            {
                                var val = createdAtProp.GetValue(u);
                                registered = val is DateTime dt ? dt.ToString("yyyy-MM-dd") : val?.ToString() ?? "-";
                            }
                            else
                            {
                                registered = "-";
                            }

                            table.Cell().Text(index++.ToString());
                            table.Cell().Text(u.Username ?? "-");
                            table.Cell().Text(u.Email ?? "-");
                            string roleText = (u.GetType().GetProperty("Role")?.GetValue(u) as string)
                                              ?? (u.GetType().GetProperty("Role")?.GetValue(u)?.ToString())
                                              ?? (u.GetType().GetProperty("RoleId")?.GetValue(u)?.ToString())
                                              ?? "-";
                            table.Cell().Text(roleText);
                            table.Cell().Text(registered);
                        }
                    });

                    col.Item().PaddingTop(25).Text("User Activity").FontSize(18).Bold();

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(30);
                            columns.RelativeColumn();
                            columns.ConstantColumn(60);
                            columns.RelativeColumn();
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("#").Bold();
                            header.Cell().Text("User").Bold();
                            header.Cell().Text("Messages").Bold();
                            header.Cell().Text("Last Active").Bold();
                        });

                        int i = 1;
                        foreach (var a in activity)
                        {
                            var u = users.FirstOrDefault(x => x.Id == a.UserId);
                            var username = u?.Username ?? $"User#{a.UserId}";
                            table.Cell().Text(i++.ToString());
                            table.Cell().Text(username);
                            table.Cell().Text(a.Count.ToString());
                            table.Cell().Text(a.LastActive.ToString("yyyy-MM-dd HH:mm"));
                        }
                    });

                    col.Item().PaddingTop(20).LineHorizontal(1);
                    col.Item().PaddingTop(20).Text("Chat Log").FontSize(18).Bold();

                    foreach (var m in messages)
                    {
                        var when = m.SentAt;
                        var who = m.User?.Username ?? "Unknown";
                        var room = m.Room?.Name ?? "General";
                        var text = m.Text ?? "";

                        col.Item().Text($"[{when:yyyy-MM-dd HH:mm}] {who} → {room}: {text}");
                    }
                });

                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("WSChat • Auto-generated • Page ");
                    txt.CurrentPageNumber();
                });
            });
        });

        return document.GeneratePdf();
    }
}
