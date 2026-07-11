using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using WSChat.Infrastructure.Data;

namespace WSChat.Infrastructure;

public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();

        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Path.Combine("..", "WSChat.Server", "appsettings.json"), optional: true)
            .Build();

        var cs = config.GetConnectionString("DefaultConnection") ?? "Data Source=wschat.db";

        var optionsBuilder = new DbContextOptionsBuilder<ChatDbContext>();
        optionsBuilder.UseSqlite(cs);

        return new ChatDbContext(optionsBuilder.Options);
    }
}
