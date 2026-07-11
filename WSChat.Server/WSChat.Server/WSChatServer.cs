using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WSChat.Application.Services;
using WSChat.Domain.Models;
using WSChat.Infrastructure.Data;
using WSChat.Infrastructure.Reports;
using WSChat.Shared;
using static WSChat.Application.Services.AuthService;

namespace WSChat.Server;

public class WSChatServer
{
    private static AuthService? _authService;
    private static ActivityLogService? _activityLogService;
    private static EmailService? _emailService;
    private static ReportService? _reportService;
    private static WeatherService? _weatherService;     
    private static NewsService? _newsService;           
    private static JokeService? _jokeService;
    private static ServiceProvider? _serviceProvider;
    private static IConfiguration? _configuration;
    private static ILogger? _logger;

    private static readonly List<WebSocket> Clients = new();
    private static readonly Dictionary<WebSocket, string> UserNames = new();
    private static readonly Dictionary<WebSocket, string> ClientRooms = new();
    private static readonly HashSet<string> Rooms = new() { "General" };
    private static readonly ConcurrentDictionary<string, List<string>> RoomHistory = new();
    private static readonly HashSet<string> Admins = new() { "admin" };
    private static UdpClient? _udpServer;
    private static readonly ConcurrentDictionary<string, IPEndPoint> _udpUsers = new();

    private static readonly List<string> ForbiddenWords = new()
    {
        "Fuck", "Shit", "Ass", "Bitch", "Damn", "Cunt", "Dick",
        "Piss", "Cock", "Motherfucker", "Bastard", "Tits", "Prick"
    };

    public static async Task StartServer()
    {
        var services = new ServiceCollection();
        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfiguration>(_configuration);

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });


        services.AddDbContext<ChatDbContext>(options =>
            options.UseSqlite(_configuration.GetConnectionString("DefaultConnection")));

        services.AddScoped<AuthService>();
        services.AddScoped<ActivityLogService>();
        services.AddScoped<ReportService>();
        services.AddScoped<EmailService>();

        services.AddHttpClient<WeatherService>();
        services.AddHttpClient<NewsService>();
        services.AddHttpClient<JokeService>();

        services.AddScoped<WeatherService>();
        services.AddScoped<NewsService>();
        services.AddScoped<JokeService>();

        _serviceProvider = services.BuildServiceProvider();

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<WSChatServer>();

        _authService = _serviceProvider.GetRequiredService<AuthService>();
        _activityLogService = _serviceProvider.GetRequiredService<ActivityLogService>();
        _emailService = _serviceProvider.GetRequiredService<EmailService>();
        _reportService = _serviceProvider.GetRequiredService<ReportService>();
        _weatherService = _serviceProvider.GetRequiredService<WeatherService>();
        _newsService = _serviceProvider.GetRequiredService<NewsService>();
        _jokeService = _serviceProvider.GetRequiredService<JokeService>();

        _logger.LogInformation("=== WSChat Server Initialization ===");

        await InitializeDatabase();

        await LoadRoomsFromDatabase();
        await BroadcastRoomsList();

        var httpListener = new HttpListener();
        httpListener.Prefixes.Add("http://localhost:5000/chat/");
        httpListener.Prefixes.Add("http://localhost:5000/reports/");
        try
        {
            httpListener.Start();
            _logger.LogInformation("Starting HttpListener...");
        }
        catch (HttpListenerException ex)
        {
            _logger.LogCritical(ex, "HttpListener failed to start. Try running as Administrator.");
            throw;
        }


        _udpServer = new UdpClient(5002);
        _ = Task.Run(UdpServerLoop);

        _logger.LogInformation("=== WSChat Server Started ===");
        _logger.LogInformation("WebSocket endpoint: ws://localhost:5000/chat/");
        _logger.LogInformation("Reports API: http://localhost:5000/reports/generate");
        _logger.LogInformation("UDP Voice server: port 5002");
        _logger.LogInformation("===============================");

        while (true)
        {
            try
            {
                var context = await httpListener.GetContextAsync();

                if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath.StartsWith("/chat") == true)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var socket = wsContext.WebSocket;

                    lock (Clients)
                    {
                        Clients.Add(socket);
                        ClientRooms[socket] = "General";
                    }

                    _logger.LogInformation($"New WebSocket client connected. Total clients: {Clients.Count}");
                    _ = Task.Run(() => HandleClient(socket));
                    continue;
                }

                if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/reports/generate")
                {
                    await HandleReportRequest(context);
                    continue;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main server loop");
            }
        }
    }

    private static async Task InitializeDatabase()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        try
        {
            _logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync();

            var adminRole = await db.Roles.FirstAsync(r => r.Name == "Admin");
            var adminUser = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin");

            if (adminUser == null)
            {
                adminUser = new WSChat.Domain.Models.User
                {
                    Username = "admin",
                    Email = "admin@chat.local",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin")
                };

                db.Users.Add(adminUser);
                await db.SaveChangesAsync();

                db.UserRoles.Add(new UserRole
                {
                    UserId = adminUser.Id,
                    RoleId = adminRole.Id
                });

                await db.SaveChangesAsync();
            }

            var generalRoom = await db.Rooms.FirstOrDefaultAsync(r => r.Name == "General");
            if (generalRoom == null)
            {
                _logger.LogInformation("Creating default 'General' room...");
                db.Rooms.Add(new Room
                {
                    Name = "General",
                    CreatedByUserId = adminUser.Id
                });
                await db.SaveChangesAsync();
            }

            if (!await db.RoomUsers.AnyAsync(ru => ru.RoomId == generalRoom.Id && ru.UserId == adminUser.Id))
            {
                db.RoomUsers.Add(new RoomUser
                {
                    RoomId = generalRoom.Id,
                    UserId = adminUser.Id
                });
                await db.SaveChangesAsync();
            }


            _logger.LogInformation("Database initialization completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Database initialization failed!");
            throw;
        }
    }

    private static async Task HandleReportRequest(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var json = await reader.ReadToEndAsync();

            var request = string.IsNullOrEmpty(json)
                ? new ReportRequest()
                : JsonSerializer.Deserialize<ReportRequest>(json) ?? new ReportRequest();

            using var scope = _serviceProvider.CreateScope();
            var reportService = scope.ServiceProvider.GetRequiredService<ReportService>();

            var pdfBytes = await reportService.GenerateReportAsync(request);
            var fileName = string.IsNullOrWhiteSpace(request.Title)
                ? "chat-report.pdf"
                : $"{request.Title.Replace(" ", "_")}.pdf";

            context.Response.ContentType = "application/pdf";
            context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            context.Response.ContentLength64 = pdfBytes.Length;

            await context.Response.OutputStream.WriteAsync(pdfBytes, 0, pdfBytes.Length);
            _logger.LogInformation($"PDF report generated: {fileName} ({pdfBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating report");
            context.Response.StatusCode = 500;
            var errorBytes = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorBytes);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static async Task HandleClient(WebSocket socket)
    {
        var buffer = new byte[4096];
        string? connectedUser = null;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogDebug($"Received from client: {json}");

                var packet = JsonSerializer.Deserialize<ChatPacket>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (packet == null) continue;

                _logger.LogInformation($"Processing packet type: {packet.Type} from: {packet.From}");

                switch (packet.Type.ToLower())
                {
                    case "register":
                        {
                            _logger.LogInformation($"Registration attempt for user: {packet.From}");

                            string email = packet.Data ?? packet.From + "@chat.local";

                            var regUser = await _authService.RegisterAsync(packet.From, email, packet.Text);
                            if (regUser == null)
                            {
                                _logger.LogWarning($"Registration failed - user already exists: {packet.From}");
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "User already exists!" });
                            }
                            else
                            {
                                lock (UserNames) { UserNames[socket] = regUser.Username; }
                                connectedUser = regUser.Username;

                                _logger.LogInformation($"User registered successfully: {regUser.Username}");
                                await SendJson(socket, new ChatPacket { Type = "register_ok", From = regUser.Username });
                                await BroadcastSystemToRoom("General", $"{regUser.Username} registered!");

                                if (!string.IsNullOrEmpty(regUser.Email))
                                {
                                    try
                                    {
                                        _logger.LogInformation($"Sending welcome email to: {regUser.Email}");
                                        var emailSent = await _emailService.SendWelcomeEmailAsync(regUser.Email, regUser.Username);
                                        if (emailSent)
                                        {
                                            _logger.LogInformation($"Welcome email sent to {regUser.Email}");
                                        }
                                        else
                                        {
                                            _logger.LogWarning($"Failed to send welcome email to {regUser.Email}");
                                        }
                                    }
                                    catch (Exception emailEx)
                                    {
                                        _logger.LogError(emailEx, "Error sending welcome email");
                                    }
                                }

                                await _activityLogService.LogActivityAsync(regUser.Id, "Auth", "Registration", $"New user {regUser.Username}");
                            }
                        }
                        break;

                    case "login":
                        {
                            _logger.LogInformation($"Login attempt for user: {packet.From}");
                            var logUser = await _authService.LoginAsync(packet.From, packet.Text);

                            if (logUser == null)
                            {
                                _logger.LogWarning($"Login failed for user: {packet.From}");
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "Invalid login" });
                            }
                            else
                            {
                                lock (UserNames) { UserNames[socket] = logUser.Username; }
                                connectedUser = logUser.Username;
                                var roleName = logUser.UserRoles.FirstOrDefault()?.Role.Name ?? "User";
                                _logger.LogInformation($"User logged in: {logUser.Username} (Role: {roleName})");

                                if (packet.Remember)
                                {
                                    var token = await _authService.GenerateRememberTokenAsync(logUser.Id);
                                    await SendJson(socket, new ChatPacket { Type = "remember_token", Text = token, Remember = true });
                                }

                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "login_ok",
                                    From = logUser.Username,
                                    Text = roleName
                                });

                                await SendMessageHistory(socket, "General");
                                await BroadcastSystemToRoom("General", $"{logUser.Username} logged in!");
                                await _activityLogService.LogActivityAsync(logUser.Id, "Auth", "Login Success", "User logged in");
                                await SendRoomsListTo(socket);
                            }
                        }
                        break;

                    case "login_with_token":
                        {
                            _logger.LogInformation("Token login attempt");
                            var token = packet.Text;
                            var user = await _authService.LoginWithTokenAsync(token);

                            if (user == null)
                            {
                                _logger.LogWarning("Invalid or expired token");
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "Invalid or expired token" });
                            }
                            else
                            {
                                UserNames[socket] = user.Username;
                                connectedUser = user.Username;
                                _logger.LogInformation($"User logged in with token: {user.Username}");

                                var roleName = user.UserRoles.FirstOrDefault()?.Role.Name ?? "User";
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "login_ok",
                                    From = user.Username,
                                    Text = roleName
                                });

                                await SendMessageHistory(socket, "General");
                                await BroadcastSystemToRoom("General", $"{user.Username} logged in!");
                            }
                            break;
                        }

                    case "udp_register":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            if (int.TryParse(packet.Text, out int port))
                            {
                                var clientIp = "127.0.0.1";

                                lock (_udpUsers)
                                {
                                    _udpUsers[userName] = new IPEndPoint(IPAddress.Parse(clientIp), port);
                                    _logger.LogInformation($"[UDP] Registered {userName} at {clientIp}:{port}");
                                }
                            }
                        }
                        break;

                    case "create":
                        {
                            var roomName = packet.Text?.Trim();
                            if (string.IsNullOrEmpty(roomName))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Room name cannot be empty." });
                                break;
                            }

                            var username = UserNames.TryGetValue(socket, out var name) ? name : null;
                            if (username == null)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "User not found." });
                                break;
                            }

                            var user = await _authService.GetUserByUsernameAsync(username);
                            if (user == null)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "User not found in DB." });
                                break;
                            }

                            _logger.LogInformation($"Room creation attempt by {username}: {roomName}");

                            using var scope = _serviceProvider.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                            var existsInDb = await db.Rooms.AnyAsync(r => r.Name == roomName);

                            if (!existsInDb)
                            {
                                var room = new Room
                                {
                                    Name = roomName,
                                    CreatedByUserId = user.Id
                                };

                                db.Rooms.Add(room);
                                await db.SaveChangesAsync();
                                _logger.LogInformation($"Room created in DB: {roomName}");
                            }

                            lock (Rooms)
                            {
                                if (!Rooms.Contains(roomName))
                                    Rooms.Add(roomName);
                            }

                            await BroadcastRoomsList();
                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"Room '{roomName}' created." });

                            await _activityLogService.LogActivityAsync(user.Id, "Room", "Create Room", $"Created room {roomName}");
                        }
                        break;

                    case "join":
                        {
                            var room = packet.Text?.Trim();
                            if (string.IsNullOrEmpty(room))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "Room name cannot be empty." });
                                break;
                            }

                            bool exists;
                            lock (Rooms) { exists = Rooms.Contains(room); }
                            if (!exists)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", Text = $"Room '{room}' does not exist." });
                                break;
                            }

                            string previousRoom;
                            string currentRoom;

                            lock (ClientRooms)
                            {
                                currentRoom = ClientRooms.GetValueOrDefault(socket, "General")!;
                            }

                            if (currentRoom == room)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", Text = $"You are already in room '{room}'" });
                                break;
                            }

                            previousRoom = currentRoom;

                            lock (ClientRooms)
                            {
                                ClientRooms[socket] = room;
                            }

                            if (UserNames.TryGetValue(socket, out var username))
                            {
                                var user = await _authService.GetUserByUsernameAsync(username);
                                if (user != null)
                                {
                                    await AddUserToRoom(user.Id, room);
                                    _logger.LogInformation($"User {username} joined room {room}");
                                    await _activityLogService.LogActivityAsync(user.Id, "Room", "Join Room", $"Joined room {room}");
                                }
                            }

                            await SendMessageHistory(socket, room);
                            await SendInMemoryHistory(socket, room);

                            await SendJson(socket, new ChatPacket { Type = "system", Text = $"You joined '{room}'" });

                            var displayName = UserNames.ContainsKey(socket) ? UserNames[socket] : "Unknown";

                            await BroadcastSystemToRoom(previousRoom, $"{displayName} left the room");
                            await BroadcastSystemToRoom(room, $"{displayName} joined the room");
                        }
                        break;

                    case "admin_list":
                        {
                            if (!UserNames.TryGetValue(socket, out var adminName) || !Admins.Contains(adminName))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "Access denied." });
                                break;
                            }

                            var activeUsers = string.Join(",", UserNames.Values);
                            _logger.LogInformation($"Admin list requested by {adminName}. Active users: {activeUsers}");
                            await SendJson(socket, new ChatPacket
                            {
                                Type = "admin_list",
                                Text = activeUsers
                            });
                            break;
                        }

                    case "kick":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admins.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var target = packet.Text?.Trim();
                            if (string.IsNullOrEmpty(target))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Specify user to kick." });
                                break;
                            }

                            _logger.LogInformation($"Kick attempt by {admin} on {target}");
                            var victim = UserNames.FirstOrDefault(x => x.Value == target).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = "You were kicked by admin." });
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kicked by admin", CancellationToken.None);

                                lock (Clients)
                                {
                                    Clients.Remove(victim);
                                }
                                lock (UserNames)
                                {
                                    UserNames.Remove(victim);
                                }

                                _logger.LogInformation($"User {target} kicked by {admin}");
                                await BroadcastSystemToRoom("General", $"{target} was kicked by admin");
                            }
                            else
                            {
                                _logger.LogWarning($"Kick failed - user not found: {target}");
                            }
                        }
                        break;

                    case "ban":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admins.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var parts = packet.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts == null || parts.Length < 2 || !int.TryParse(parts[1], out int minutes))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Usage: /ban <username> <minutes>" });
                                break;
                            }

                            var targetUsers = parts[0];
                            var until = DateTime.UtcNow.AddMinutes(minutes);

                            _logger.LogInformation($"Ban attempt by {admin} on {targetUsers} for {minutes} minutes");
                            var victim = UserNames.FirstOrDefault(x => x.Value == targetUsers).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = $"You are banned until {until.ToLocalTime()}" });
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Banned by admin", CancellationToken.None);

                                lock (Clients)
                                {
                                    Clients.Remove(victim);
                                }
                                lock (UserNames)
                                {
                                    UserNames.Remove(victim);
                                }

                                _logger.LogInformation($"User {targetUsers} banned by {admin} until {until.ToLocalTime()}");
                                await BroadcastSystemToRoom("General", $"{targetUsers} was banned by {admin} until {until.ToLocalTime()}");
                            }
                        }
                        break;

                    case "admin_ban":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admins.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var target = packet.To?.Trim();
                            if (string.IsNullOrEmpty(target) || !int.TryParse(packet.Text, out int minutes))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Invalid parameters." });
                                break;
                            }

                            _logger.LogInformation($"Admin ban attempt by {admin} on {target} for {minutes} minutes");
                            var banResult = await _authService.BanUserAsync(target, admin, minutes);

                            if (banResult == BanResult.UserNotFound)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "User not found." });
                                break;
                            }

                            if (banResult == BanResult.AdminNotFound)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Error: admin not found." });
                                break;
                            }

                            var victim = UserNames.FirstOrDefault(x => x.Value == target).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = $"You are banned{(minutes > 0 ? $" until {DateTime.UtcNow.AddMinutes(minutes):yyyy-MM-dd HH:mm} UTC" : " permanently")}" });
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Banned", CancellationToken.None);
                                lock (Clients) Clients.Remove(victim);
                                lock (UserNames) UserNames.Remove(victim);
                                lock (ClientRooms) ClientRooms.Remove(victim);
                                _logger.LogInformation($"User {target} disconnected due to ban");
                            }

                            await BroadcastSystemToRoom("General", $"{target} was banned by {admin}{(minutes > 0 ? $" for {minutes} minutes" : " permanently")}");
                            await SendUpdatedAdminList();
                            var adminUser = await _authService!.GetUserByUsernameAsync(admin);
                            if (adminUser != null)
                            {
                                await _activityLogService!.LogActivityAsync(
                                    adminUser.Id,
                                    "Moderation",
                                    "Ban User",
                                    $"Banned user: {target} for {minutes} minutes."
                                );
                            }
                            break;
                        }

                    case "admin_delete":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admins.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var target = packet.To?.Trim();
                            if (string.IsNullOrEmpty(target))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Specify user." });
                                break;
                            }

                            _logger.LogInformation($"Admin delete attempt by {admin} on {target}");
                            bool deleted = await _authService.DeleteUserAsync(target);

                            if (!deleted)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "User not found." });
                                break;
                            }

                            var victim = UserNames.FirstOrDefault(x => x.Value == target).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = "Your account has been permanently deleted." });
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Deleted", CancellationToken.None);
                                lock (Clients) Clients.Remove(victim);
                                lock (UserNames) UserNames.Remove(victim);
                                lock (ClientRooms) ClientRooms.Remove(victim);
                                _logger.LogInformation($"User {target} disconnected due to account deletion");
                            }

                            await BroadcastSystemToRoom("General", $"{target} was permanently deleted by {admin}.");
                            await SendUpdatedAdminList();
                            var adminUser = await _authService!.GetUserByUsernameAsync(admin);
                            if (adminUser != null)
                            {
                                await _activityLogService!.LogActivityAsync(
                                    adminUser.Id,
                                    "Moderation",
                                    "Delete User",
                                    $"Deleted user: {target}."
                                );
                            }
                            break;
                        }

                    case "weather":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            var city = packet.Text?.Trim();
                            if (string.IsNullOrEmpty(city))
                            {
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = "Usage: /weather <city> - Get weather information"
                                });
                                break;
                            }

                            try
                            {
                                _logger.LogInformation($"Weather request from {userName} for {city}");

                                var weatherInfo = await WeatherService.GetWeatherAsync(city);

                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = weatherInfo
                                });

                                var user = await _authService.GetUserByUsernameAsync(userName);
                                if (user != null)
                                {
                                    await _activityLogService.LogActivityAsync(
                                        user.Id,
                                        "API",
                                        "Weather",
                                        $"Requested weather for {city}"
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Weather API error for {city}");
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = $"Could not get weather for {city}. Please try again."
                                });
                            }
                        }
                        break;

                    case "news":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            var category = packet.Text?.Trim()?.ToLower();
                            if (string.IsNullOrEmpty(category))
                            {
                                category = "general";
                            }

                            var validCategories = new[] { "general", "technology", "business", "sports", "entertainment" };
                            if (!validCategories.Contains(category))
                            {
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = $"Invalid category. Available: {string.Join(", ", validCategories)}"
                                });
                                break;
                            }

                            try
                            {
                                _logger.LogInformation($"News request from {userName} in category {category}");

                                var newsInfo = await _newsService.GetNewsAsync(category);

                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = newsInfo
                                });

                                var user = await _authService.GetUserByUsernameAsync(userName);
                                if (user != null)
                                {
                                    await _activityLogService.LogActivityAsync(
                                        user.Id,
                                        "API",
                                        "News",
                                        $"Requested {category} news"
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"News API error for category {category}");
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = $"Could not get {category} news. Please try again."
                                });
                            }
                        }
                        break;

                    case "joke":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            var jokeType = packet.Text?.Trim()?.ToLower();

                            try
                            {
                                _logger.LogInformation($"Joke request from {userName}, type: {jokeType ?? "random"}");

                                string joke;
                                if (jokeType == "programming" || jokeType == "code")
                                {
                                    joke = await _jokeService.GetProgrammingJokeAsync();
                                }
                                else
                                {
                                    joke = await _jokeService.GetRandomJokeAsync();
                                }

                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = joke
                                });

                                var user = await _authService.GetUserByUsernameAsync(userName);
                                if (user != null)
                                {
                                    await _activityLogService.LogActivityAsync(
                                        user.Id,
                                        "API",
                                        "Joke",
                                        "Requested a joke"
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Joke API error");
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = "Could not get a joke. Please try again."
                                });
                            }
                        }
                        break;

                    case "quote":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            try
                            {
                                _logger.LogInformation($"Quote request from {userName}");

                                var quote = await _jokeService.GetQuoteAsync();

                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = quote
                                });

                                var user = await _authService.GetUserByUsernameAsync(userName);
                                if (user != null)
                                {
                                    await _activityLogService.LogActivityAsync(
                                        user.Id,
                                        "API",
                                        "Quote",
                                        "Requested a quote"
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Quote API error");
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = "Could not get a quote. Please try again."
                                });
                            }
                        }
                        break;

                    case "send_email":
                        {
                            _logger.LogInformation($"Email sending request from: {packet.From}");

                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            try
                            {
                                var parts = packet.Text?.Split('|', 3);
                                if (parts == null || parts.Length < 3)
                                {
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = "Usage: /email <username>|<subject>|<message> - Send email (admin only)"
                                    });
                                    break;
                                }

                                var toUsername = parts[0].Trim();
                                var subject = parts[1].Trim();
                                var message = parts[2].Trim();

                                var currentUser = await _authService.GetUserByUsernameAsync(userName);
                                if (currentUser == null ||
                                    !currentUser.UserRoles.Any(ur => ur.Role.Name == "Admin"))
                                {
                                    _logger.LogWarning($"Unauthorized email attempt by {userName}");
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = "Access denied. Admin only."
                                    });
                                    break;
                                }

                                var targetUser = await _authService.GetUserByUsernameAsync(toUsername);
                                if (targetUser == null)
                                {
                                    _logger.LogWarning($"Email target not found: {toUsername}");
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = $"User '{toUsername}' not found."
                                    });
                                    break;
                                }

                                if (string.IsNullOrEmpty(targetUser.Email))
                                {
                                    _logger.LogWarning($"User {toUsername} has no email");
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = $"User '{toUsername}' has no email address."
                                    });
                                    break;
                                }

                                _logger.LogInformation($"Attempting to send email to {targetUser.Email} from {userName}");

                                var success = await _emailService.SendEmailAsync(targetUser.Email, subject, message);

                                if (success)
                                {
                                    _logger.LogInformation($"Email sent successfully to {targetUser.Email}");
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = $"Email sent to {targetUser.Email}"
                                    });

                                    await _activityLogService.LogActivityAsync(
                                        currentUser.Id,
                                        "Email",
                                        "Send Email",
                                        $"Sent email to {targetUser.Username} ({targetUser.Email})"
                                    );
                                }
                                else
                                {
                                    _logger.LogWarning($"Failed to send email to {targetUser.Email}");
                                    await SendJson(socket, new ChatPacket
                                    {
                                        Type = "system",
                                        Text = "Failed to send email."
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Email sending error");
                                await SendJson(socket, new ChatPacket
                                {
                                    Type = "system",
                                    Text = "Email service error."
                                });
                            }
                        }
                        break;

                    case "leave":
                        {
                            string prev;
                            lock (ClientRooms)
                            {
                                prev = ClientRooms.GetValueOrDefault(socket, "General")!;
                                ClientRooms[socket] = "General";
                            }
                            _logger.LogInformation($"User left room {prev} and joined General");
                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"You left room: {prev}. Now in General." });
                            await BroadcastSystemToRoom(prev, $"{UserNames.GetValueOrDefault(socket, "Unknown")} left the room");
                            await BroadcastSystemToRoom("General", $"{UserNames.GetValueOrDefault(socket, "Unknown")} joined General");
                        }
                        break;

                    case "list_rooms":
                        await SendRoomsListTo(socket);
                        break;

                    case "who":
                        {
                            string roomNow;
                            lock (ClientRooms) { roomNow = ClientRooms.GetValueOrDefault(socket, "General")!; }
                            var users = GetUsersInRoom(roomNow);
                            string text = string.Join(", ", users);
                            _logger.LogDebug($"WHO request for room {roomNow}: {text}");
                            await SendJson(socket, new ChatPacket { Type = "who", From = "server", Text = text });
                        }
                        break;

                    case "message":
                    case "chat_message":
                        {
                            if (!UserNames.TryGetValue(socket, out var userName)) break;

                            string msg = packet.Text ?? "";
                            string censored = CensorMessage(msg);

                            _logger.LogInformation($"Message from {userName}: {msg} (censored: {censored})");

                            if (!string.IsNullOrWhiteSpace(msg) && msg.StartsWith("/"))
                            {
                                var parts = msg.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                                var cmd = parts[0].ToLowerInvariant();

                                switch (cmd)
                                {
                                    case "/help":
                                        await SendSystemToUser(userName,
                                            "📚 Available Commands:\n" +
                                            "─────────────────────\n" +
                                            "📱 Basic:\n" +
                                            "/rooms - list all chat rooms\n" +
                                            "/join <room> - join a room\n" +
                                            "/leave - leave current room\n" +
                                            "/who - show users in current room\n" +
                                            "/history - show recent messages\n" +
                                            "/pm <user> <message> - private message\n" +
                                            "\n" +
                                            "🎮 Entertainment:\n" +
                                            "/joke [programming] - get a joke\n" +
                                            "/quote - get inspirational quote\n" +
                                            "/weather <city> - get weather info\n" +
                                            "/news [category] - get news\n" +
                                            "\n" +
                                            "🛠️ Admin Commands:\n" +
                                            "/ban <user> <minutes> - ban user\n" +
                                            "/kick <user> - kick user\n" +
                                            "/email <user>|<subject>|<message> - send email\n" +
                                            "\n" +
                                            "📊 Reports:\n" +
                                            "Visit http://localhost:5000/reports/generate\n" +
                                            "\n" +
                                            "Need help? Type /help <command> for details");
                                        break;

                                    case "/history":
                                        var currentRoom = ClientRooms.GetValueOrDefault(socket, "General")!;
                                        await SendInMemoryHistory(socket, currentRoom);
                                        break;

                                    case "/rooms":
                                        await SendRoomsListTo(socket);
                                        break;

                                    case "/pm":
                                        if (parts.Length >= 3)
                                        {
                                            var to = parts[1];
                                            var text = parts[2];
                                            await SendPrivateMessage(userName, to, text);
                                        }
                                        else
                                        {
                                            await SendSystemToUser(userName, "Usage: /pm <user> <message>");
                                        }
                                        break;

                                    default:
                                        await SendSystemToUser(userName, $"Unknown command: {cmd}. Use /help");
                                        break;
                                }

                                break;
                            }

                            if (censored != msg)
                            {
                                _logger.LogWarning($"Message censored from {userName}: {msg}");
                                var snapshot = UserNames.ToArray();
                                foreach (var kv in snapshot)
                                {
                                    if (Admins.Contains(kv.Value) && kv.Key.State == WebSocketState.Open)
                                        await SendJson(kv.Key, new ChatPacket { Type = "censor_warning", From = userName, Text = msg });
                                }
                            }

                            DateTime timestamp = DateTime.UtcNow;

                            try
                            {
                                using var scope = _serviceProvider.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == userName);
                                var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == ClientRooms.GetValueOrDefault(socket, "General"));

                                if (user != null && room != null)
                                {
                                    var dbMessage = new Message
                                    {
                                        UserId = user.Id,
                                        RoomId = room.Id,
                                        Text = censored,
                                        SentAt = DateTime.UtcNow
                                    };
                                    db.Messages.Add(dbMessage);
                                    await db.SaveChangesAsync();
                                    _logger.LogDebug($"Message saved to DB: {userName} -> {room.Name}");
                                }

                                var currentRoomName = ClientRooms.GetValueOrDefault(socket, "General")!;
                                RoomHistory.AddOrUpdate(
                                    currentRoomName,
                                    (_) => new List<string> { $"{userName}: {censored}" },
                                    (_, existing) =>
                                    {
                                        existing.Add($"{userName}: {censored}");
                                        if (existing.Count > 200) existing.RemoveAt(0);
                                        return existing;
                                    });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to save message to DB");
                            }

                            await BroadcastMessage(userName, censored, socket, timestamp);
                            break;
                        }

                    case "private":
                        if (!UserNames.TryGetValue(socket, out var fromName))
                        {
                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "You are not authenticated." });
                        }
                        else
                        {
                            await SendPrivateMessage(fromName, packet.To, packet.Text);
                        }
                        break;

                    case "ping":
                        await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "pong" });
                        break;

                    default:
                        _logger.LogWarning($"Unknown packet type: {packet.Type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"HandleClient error for user {connectedUser}");
        }
        finally
        {
            await CleanupClient(socket, connectedUser);
        }
    }

    private static async Task CleanupClient(WebSocket socket, string? connectedUser)
    {
        lock (Clients) Clients.Remove(socket);
        lock (UserNames) UserNames.Remove(socket);
        lock (ClientRooms) ClientRooms.Remove(socket);

        if (connectedUser != null)
        {
            _logger.LogInformation($"User {connectedUser} disconnected");
            await BroadcastSystemToRoom("General", $"{connectedUser} disconnected");
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
            _logger.LogDebug("WebSocket closed normally");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing WebSocket");
        }

        _logger.LogInformation($"Client disconnected. Total clients: {Clients.Count}");
    }

    private static async Task UdpServerLoop()
    {
        _logger.LogInformation("[UDP] Server listening on port 5002...");

        while (true)
        {
            try
            {
                var result = await _udpServer.ReceiveAsync();
                _logger.LogDebug($"[UDP] Received {result.Buffer.Length} bytes from {result.RemoteEndPoint}");

                var json = Encoding.UTF8.GetString(result.Buffer);
                var packet = JsonSerializer.Deserialize<VoicePacket>(json);

                if (packet?.To == null || packet.From == null)
                {
                    _logger.LogWarning("[UDP] Invalid packet");
                    continue;
                }

                _udpUsers[packet.From] = result.RemoteEndPoint;

                if (!_udpUsers.TryGetValue(packet.To, out var targetEndPoint))
                {
                    _logger.LogWarning($"[UDP] Target not registered: {packet.To}");
                    continue;
                }

                await _udpServer.SendAsync(
                    result.Buffer,
                    result.Buffer.Length,
                    targetEndPoint
                );

                _logger.LogInformation($"[UDP] Voice forwarded from {packet.From} to {packet.To}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UDP] Error in UDP loop");
            }
        }
    }

    private static async Task SendUpdatedAdminList()
    {
        KeyValuePair<WebSocket, string>[] snapshot;
        lock (UserNames) snapshot = UserNames.ToArray();

        var list = string.Join(",", snapshot.Select(k => k.Value));
        _logger.LogDebug($"Sending admin list update: {list}");

        foreach (var kvp in snapshot)
        {
            if (Admins.Contains(kvp.Value) && kvp.Key?.State == WebSocketState.Open)
            {
                await SendJson(kvp.Key, new ChatPacket { Type = "admin_list", Text = list });
            }
        }
    }

    private static async Task LoadRoomsFromDatabase()
    {
        using var scope = _serviceProvider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var dbRooms = await db.Rooms.Select(r => r.Name).ToListAsync();

        lock (Rooms)
        {
            Rooms.Clear();
            foreach (var r in dbRooms) Rooms.Add(r);
        }

        _logger.LogInformation($"Loaded {Rooms.Count} rooms from DB: {string.Join(", ", Rooms)}");
    }

    private static async Task BroadcastRoomsList()
    {
        using var scope = _serviceProvider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var dbRooms = await db.Rooms
            .Select(r => r.Name)
            .ToListAsync();

        var roomsText = string.Join(",", dbRooms);

        var pkt = new ChatPacket
        {
            Type = "rooms",
            Text = roomsText
        };

        _logger.LogDebug($"Broadcasting rooms list: {roomsText}");

        KeyValuePair<WebSocket, string>[] snapshot;
        lock (Clients) snapshot = Clients.Select(c => new KeyValuePair<WebSocket, string>(c, "")).ToArray();

        foreach (var kv in snapshot)
        {
            var client = kv.Key;
            try
            {
                await SendJson(client, pkt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error sending rooms list to client");
            }
        }

        lock (Rooms)
        {
            Rooms.Clear();
            foreach (var r in dbRooms) Rooms.Add(r);
        }
    }

    private static async Task SendRoomsListTo(WebSocket socket)
    {
        using var scope = _serviceProvider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var dbRooms = await db.Rooms.Select(r => r.Name).ToListAsync();
        var text = string.Join(",", dbRooms);
        _logger.LogDebug($"Sending rooms list to client: {text}");
        await SendJson(socket, new ChatPacket { Type = "rooms", Text = text });
    }

    private static IEnumerable<string> GetUsersInRoom(string room)
    {
        var result = new List<string>();
        lock (ClientRooms)
        {
            foreach (var kv in ClientRooms)
            {
                if (kv.Value == room)
                {
                    if (UserNames.TryGetValue(kv.Key, out var name))
                        result.Add(name);
                }
            }
        }
        return result;
    }

    private static async Task BroadcastSystemToRoom(string room, string text)
    {
        var packet = new ChatPacket { Type = "system", Text = text, To = room, From = "server" };
        _logger.LogInformation($"System message to room {room}: {text}");
        var json = JsonSerializer.Serialize(packet);
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        List<WebSocket> clientsCopy;
        Dictionary<WebSocket, string> roomsSnapshot;
        lock (Clients) { clientsCopy = Clients.ToList(); }
        lock (ClientRooms) { roomsSnapshot = new Dictionary<WebSocket, string>(ClientRooms); }

        var targets = clientsCopy.Where(c => roomsSnapshot.TryGetValue(c, out var r) && r == room).ToList();
        _logger.LogDebug($"System message targets in room {room}: {targets.Count} clients");

        foreach (var client in targets)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error sending system message to client");
                }
            }
        }
    }

    private static async Task BroadcastMessage(string from, string text, WebSocket sender, DateTime timestamp)
    {
        string? room;
        lock (ClientRooms) { ClientRooms.TryGetValue(sender, out room); }
        if (string.IsNullOrEmpty(room)) room = "General";

        var pkt = new ChatPacket { Type = "message", From = from, Text = text, Timestamp = timestamp };
        _logger.LogDebug($"Broadcasting message from {from} to room {room}: {text}");

        string json = JsonSerializer.Serialize(pkt);
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        List<WebSocket> clientsCopy;
        Dictionary<WebSocket, string> roomsSnapshot;
        lock (Clients) { clientsCopy = Clients.ToList(); }
        lock (ClientRooms) { roomsSnapshot = new Dictionary<WebSocket, string>(ClientRooms); }

        var targets = clientsCopy.Where(c => roomsSnapshot.TryGetValue(c, out var r) && r == room).ToList();

        foreach (var client in targets)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error broadcasting message to client");
                }
            }
        }
    }

    private static async Task SendJson(WebSocket socket, ChatPacket pkt)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
            };
            var json = JsonSerializer.Serialize(pkt, options);
            _logger.LogDebug($"Sending JSON: {json}");

            var buffer = Encoding.UTF8.GetBytes(json);
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else
            {
                _logger.LogWarning($"Cannot send - socket not open. State: {socket.State}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JSON to client");
        }
    }

    private static async Task AddUserToRoom(int userId, string roomName)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == roomName);
        if (room == null) return;

        var exists = await db.RoomUsers.AnyAsync(ru => ru.RoomId == room.Id && ru.UserId == userId);
        if (!exists)
        {
            db.RoomUsers.Add(new RoomUser
            {
                RoomId = room.Id,
                UserId = userId,
                JoinedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            _logger.LogDebug($"User {userId} added to room {roomName} in DB");
        }
    }

    private static async Task SendSystemToUser(string username, string text)
    {
        var targetSocket = UserNames.FirstOrDefault(kv => kv.Value == username).Key;
        if (targetSocket != null && targetSocket.State == WebSocketState.Open)
        {
            _logger.LogDebug($"Sending system message to {username}: {text}");
            await SendJson(targetSocket, new ChatPacket { Type = "system", From = "server", Text = text });
        }
        else
        {
            _logger.LogWarning($"Cannot send system message to {username} - not connected");
        }
    }

    private static async Task SendInMemoryHistory(WebSocket socket, string roomName)
    {
        if (RoomHistory.TryGetValue(roomName, out var list))
        {
            _logger.LogDebug($"Sending in-memory history for room {roomName}: {list.Count} messages");
            foreach (var line in list)
            {
                var sep = line.IndexOf(':');
                var from = sep > 0 ? line.Substring(0, sep) : "Unknown";
                var text = sep > 0 ? line.Substring(sep + 1).Trim() : line;

                var pkt = new ChatPacket
                {
                    Type = "message",
                    From = from,
                    Text = text,
                };
                await SendJson(socket, pkt);
            }
        }
        else
        {
            _logger.LogDebug($"No in-memory history for room {roomName}");
        }
    }

    private static async Task SendPrivateMessage(string from, string to, string text)
    {
        var pkt = new ChatPacket { Type = "private", From = from, To = to, Text = text };
        _logger.LogInformation($"Private message from {from} to {to}: {text}");

        string json = JsonSerializer.Serialize(pkt);
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        var target = UserNames.FirstOrDefault(x => x.Value == to).Key;
        if (target != null && target.State == WebSocketState.Open)
        {
            await target.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else
        {
            _logger.LogWarning($"Private message failed - user {to} not online");
            var senderSocket = UserNames.FirstOrDefault(x => x.Value == from).Key;
            if (senderSocket != null && senderSocket.State == WebSocketState.Open)
            {
                await SendJson(senderSocket, new ChatPacket { Type = "system", From = "server", Text = $"User {to} is not online" });
            }
        }
    }

    private static async Task SendMessageHistory(WebSocket socket, string roomName)
    {
        using var scope = _serviceProvider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Name == roomName);
        if (room == null) return;

        var history = await db.Messages
            .Where(m => m.RoomId == room.Id)
            .OrderBy(m => m.SentAt)
            .Take(50)
            .Include(m => m.User)
            .Select(m => new ChatPacket
            {
                Type = "message",
                From = m.User.Username,
                Text = m.Text,
                Timestamp = m.SentAt
            })
            .ToListAsync();

        _logger.LogDebug($"Sending DB history for room {roomName}: {history.Count} messages");

        foreach (var pkt in history)
            await SendJson(socket, pkt);
    }

    private static string CensorMessage(string message)
    {
        string censor = message;

        foreach (var word in ForbiddenWords)
        {
            var pattern = "\\b" + Regex.Escape(word) + "\\b";
            censor = Regex.Replace(censor, pattern, "###", RegexOptions.IgnoreCase);
        }

        if (censor != message)
        {
            _logger.LogWarning($"Message censored: {message} -> {censor}");
        }

        return censor;
    }
}