using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WSChat.Shared;

namespace WSChat.Server;

public class WSChatServer
{
    private static readonly List<WebSocket> Clients = new();
    private static readonly Dictionary<WebSocket, string> UserNames = new();
    private static readonly Dictionary<string, string> RegisteredUsers = new();
    private static readonly Dictionary<WebSocket, string> ClientRooms = new();
    private static readonly HashSet<string> Rooms = new() { "General" };
    private static readonly HashSet<string> Admin = new() { "admin" };
    private static readonly Dictionary<string, DateTime> BannedUsers = new();
    private static readonly HashSet<string> DeletedUsers = new();
    private static readonly List<string> ForbiddenWords = new()
    {
        "Fuck",
        "Shit",
        "Ass",
        "Bitch",
        "Damn",
        "Cunt",
        "Dick",
        "Piss",
        "Cock",
        "Motherfucker",
        "Bastard",
        "Tits",
        "Prick"
    };

    public static async Task StartServer()
    {
        HttpListener listener = new();
        listener.Prefixes.Add("http://localhost:5000/chat/");
        listener.Start();
        Console.WriteLine("Server started on ws://localhost:5000/chat/");

        while (true)
        {
            var context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var socket = wsContext.WebSocket;
                lock (Clients)
                {
                    Clients.Add(socket);
                    ClientRooms[socket] = "General";
                }
                Console.WriteLine("Client connected");

                _ = HandleClient(socket);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
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
                Console.WriteLine($"[Server] <<< Received JSON: {json}");

                ChatPacket? packet = null;
                try { packet = JsonSerializer.Deserialize<ChatPacket>(json); }
                catch (Exception ex)
                {
                    Console.WriteLine("[Server] Deserialize ChatPacket failed: " + ex.Message);
                }

                if (packet == null) continue;

                switch (packet.Type)
                {
                    case "register":
                        {
                            if (DeletedUsers.Contains(packet.From))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Your account has been permanently deleted." });
                                continue;
                            }

                            if (RegisteredUsers.ContainsKey(packet.From))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"Login '{packet.From}' already exists." });
                            }
                            else
                            {
                                string hashed = BCrypt.Net.BCrypt.HashPassword(packet.Text);
                                RegisteredUsers[packet.From] = hashed;

                                lock (UserNames) { UserNames[socket] = packet.From; }
                                connectedUser = packet.From;

                                lock (ClientRooms) { if (!ClientRooms.ContainsKey(socket)) ClientRooms[socket] = "General"; }

                                await SendJson(socket, new ChatPacket { Type = "register_ok", From = packet.From, Text = "Registration successful!" });

                                string room = ClientRooms.GetValueOrDefault(socket, "General")!;
                                await BroadcastSystemToRoom(room, $"{packet.From} joined the chat");
                                await BroadcastRoomsList();
                            }
                        }
                        break;

                    case "login":
                        {
                            if (DeletedUsers.Contains(packet.From))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Your account has been permanently deleted." });
                                continue;
                            }

                            if (BannedUsers.TryGetValue(packet.From, out var banTime))
                            {
                                if (DateTime.UtcNow < banTime)
                                {
                                    await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"You are banned until {banTime} UTC." });
                                    continue;
                                }
                                else
                                {
                                    BannedUsers.Remove(packet.From);
                                }
                            }

                            if (RegisteredUsers.TryGetValue(packet.From, out var storedHash) &&
                                BCrypt.Net.BCrypt.Verify(packet.Text, storedHash))
                            {
                                lock (UserNames) { UserNames[socket] = packet.From; }
                                connectedUser = packet.From;

                                lock (ClientRooms) { if (!ClientRooms.ContainsKey(socket)) ClientRooms[socket] = "General"; }

                                await SendJson(socket, new ChatPacket { Type = "login_ok", From = packet.From, Text = "Login successful!" });

                                string room = ClientRooms.GetValueOrDefault(socket, "General")!;
                                await BroadcastSystemToRoom(room, $"{packet.From} joined the chat");
                                await BroadcastRoomsList();
                            }
                            else
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Invalid login or password." });
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

                            bool created = false;
                            lock (Rooms)
                            {
                                if (!Rooms.Contains(roomName))
                                {
                                    Rooms.Add(roomName);
                                    created = true;
                                }
                            }

                            if (created)
                            {
                                Console.WriteLine($"[Server] Room created: {roomName}");
                                await BroadcastRoomsList();
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"Room '{roomName}' created." });
                            }
                            else
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"Room '{roomName}' already exists." });
                            }
                        }
                        break;

                    case "join":
                        {
                            var room = packet.Text?.Trim();
                            if (string.IsNullOrEmpty(room))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Room name cannot be empty." });
                                break;
                            }

                            bool exists;
                            lock (Rooms) { exists = Rooms.Contains(room); }

                            if (!exists)
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"Room '{room}' does not exist." });
                                break;
                            }

                            string prevRoom;
                            lock (ClientRooms)
                            {
                                prevRoom = ClientRooms.GetValueOrDefault(socket, "General")!;
                                ClientRooms[socket] = room;
                            }

                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = $"You have joined room: {room}" });
                            await BroadcastSystemToRoom(prevRoom, $"{UserNames.GetValueOrDefault(socket, "Unknown")} left the room");
                            await BroadcastSystemToRoom(room, $"{UserNames.GetValueOrDefault(socket, "Unknown")} joined the room");
                        }
                        break;

                    case "admin_list":
                        {
                            if (!UserNames.TryGetValue(socket, out var adminName) || !Admin.Contains(adminName))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", Text = "Access denied." });
                                break;
                            }

                            var activeUsers = string.Join(",", UserNames.Values);
                            await SendJson(socket, new ChatPacket
                            {
                                Type = "admin_list",
                                Text = activeUsers
                            });
                            break;
                        }


                    case "kick":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admin.Contains(admin))
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
                                
                                await BroadcastSystemToRoom("General", $"{target} was kicked by admin");
                            }
                        }
                        break;

                    case "ban":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admin.Contains(admin))
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
                            BannedUsers[targetUsers] = until;

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

                                await BroadcastSystemToRoom("General", $"{targetUsers} was banned by {admin} until {until.ToLocalTime()}");
                            }
                        }
                        break;

                    case "admin_ban":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admin.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var target = packet.To?.Trim();
                            if (string.IsNullOrEmpty(target) || !int.TryParse(packet.Text, out int minutes))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Invalid ban parameters." });
                                break;
                            }

                            var until = DateTime.UtcNow.AddMinutes(minutes);
                            BannedUsers[target] = until;

                            var victim = UserNames.FirstOrDefault(x => x.Value == target).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = $"You are banned until {until.ToLocalTime()}" });
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Banned by admin", CancellationToken.None);

                                lock (Clients) Clients.Remove(victim);
                                lock (UserNames) UserNames.Remove(victim);
                                lock (ClientRooms) ClientRooms.Remove(victim ?? socket);
                                await BroadcastSystemToRoom("General", $"{target} was banned by {admin} until {until.ToLocalTime()}");
                            }
                            await SendUpdatedAdminList();
                            break;
                        }

                    case "admin_delete":
                        {
                            if (!UserNames.TryGetValue(socket, out var admin) || !Admin.Contains(admin))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Access denied." });
                                break;
                            }

                            var target = packet.To?.Trim();
                            if (string.IsNullOrEmpty(target))
                            {
                                await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Specify user to delete." });
                                break;
                            }

                            DeletedUsers.Add(target);
                            RegisteredUsers.Remove(target);
                            await SendUpdatedAdminList();

                            var victim = UserNames.FirstOrDefault(x => x.Value == target).Key;
                            if (victim != null)
                            {
                                await SendJson(victim, new ChatPacket { Type = "system", From = "server", Text = $"You are deleted until."});
                                await victim.CloseAsync(WebSocketCloseStatus.NormalClosure, "Deleted by admin", CancellationToken.None);

                                lock (Clients) Clients.Remove(victim);
                                lock (UserNames) UserNames.Remove(victim);
                                lock (ClientRooms) ClientRooms.Remove(victim ?? socket);
                                await BroadcastSystemToRoom("General", $"{target} was deleted by {admin}.");
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
                            await SendJson(socket, new ChatPacket { Type = "who", From = "server", Text = text });
                        }
                        break;

                    case "message":
                        if (UserNames.TryGetValue(socket, out var userName))
                        {
                            await BroadcastMessage(userName, packet.Text, socket);
                        }
                        else
                        {
                            await SendJson(socket, new ChatPacket
                            {
                                Type = "system",
                                From = "server",
                                Text = "You are not authenticated."
                            });
                        }
                        break;

                    case "chat_message":
                        {
                            string msg = packet.Text ?? "";
                            string censored = CensorMessage(msg);

                            if (censored != msg)
                            {
                                KeyValuePair<WebSocket, string>[] usersSnapshot;
                                lock (UserNames)
                                {
                                    usersSnapshot = UserNames.ToArray();
                                }

                                foreach (var kv in usersSnapshot)
                                {
                                    var adminSocket = kv.Key;
                                    var user = kv.Value;
                                    if (Admin.Contains(user) && adminSocket != null && adminSocket.State == WebSocketState.Open)
                                    {
                                        try
                                        {
                                            await SendJson(adminSocket, new ChatPacket
                                            {
                                                Type = "system",
                                                From = "server",
                                                Text = $"User '{packet.From}' sent a forbidden word: \"{msg}\""
                                            });
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }

                            await BroadcastMessage(packet.From ?? "Unknown", censored, socket);
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
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("HandleClient error: " + ex);
        }
        finally
        {
            string? roomBefore = null;
            lock (ClientRooms) { ClientRooms.TryGetValue(socket, out roomBefore); ClientRooms.Remove(socket); }

            lock (Clients) { Clients.Remove(socket); }

            if (connectedUser != null)
            {
                lock (UserNames) { UserNames.Remove(socket); }
                await BroadcastSystemToRoom(roomBefore ?? "General", $"{connectedUser} disconnected");
            }

            try { socket.Dispose(); } catch { }
            Console.WriteLine("Client disconnected");
        }
    }

    private static async Task SendUpdatedAdminList()
    {
        KeyValuePair<WebSocket, string>[] snapshot;
        lock (UserNames) snapshot = UserNames.ToArray();

        var list = string.Join(",", snapshot.Select(k => k.Value));
        foreach (var kvp in snapshot)
        {
            if (Admin.Contains(kvp.Value) && kvp.Key?.State == WebSocketState.Open)
            {
                await SendJson(kvp.Key, new ChatPacket { Type = "admin_list", Text = list });
            }
        }
    }

    private static async Task BroadcastRoomsList()
    {
        string text;
        lock (Rooms) { text = string.Join(",", Rooms); }

        var pkt = new ChatPacket { Type = "rooms", From = "server", Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Broadcasting ROOMS: {json}");
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        List<WebSocket> clientsCopy;
        lock (Clients) { clientsCopy = Clients.ToList(); }

        foreach (var client in clientsCopy)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private static async Task SendRoomsListTo(WebSocket socket)
    {
        string text;
        lock (Rooms) { text = string.Join(",", Rooms); }
        await SendJson(socket, new ChatPacket { Type = "rooms", From = "server", Text = text });
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
        var pkt = new ChatPacket { Type = "system", From = "Server", Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Broadcasting SYSTEM to room {room}: {json}");
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
                catch { }
            }
        }
    }

    private static async Task BroadcastMessage(string from, string text, WebSocket sender)
    {
        string? room;
        lock (ClientRooms) { ClientRooms.TryGetValue(sender, out room); }
        if (string.IsNullOrEmpty(room)) room = "General";

        var pkt = new ChatPacket { Type = "message", From = from, Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Broadcasting MESSAGE to room {room}: {json}");
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
                catch { }
            }
        }
    }

    private static async Task SendJson(WebSocket socket, ChatPacket pkt)
    {
        var json = JsonSerializer.Serialize(pkt);
        var buffer = Encoding.UTF8.GetBytes(json);
        Console.WriteLine($"[Server] >>> SendTo single socket: {json}");
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private static async Task SendPrivateMessage(string from, string to, string text)
    {
        var pkt = new ChatPacket { Type = "private", From = from, To = to, Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Sending PRIVATE: {json}");
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        var target = UserNames.FirstOrDefault(x => x.Value == to).Key;
        if (target != null && target.State == WebSocketState.Open)
        {
            await target.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else
        {
            var senderSocket = UserNames.FirstOrDefault(x => x.Value == from).Key;
            if (senderSocket != null && senderSocket.State == WebSocketState.Open)
            {
                await SendJson(senderSocket, new ChatPacket { Type = "system", From = "server", Text = $"User {to} is not online" });
            }
        }
    }

    private static string CensorMessage(string message)
    {
        string censor = message;

        foreach (var word in ForbiddenWords)
        {
            var pattern = "\\b" + Regex.Escape(word) + "\\b";
            censor = Regex.Replace(censor, pattern, "###", RegexOptions.IgnoreCase);
        }

        return censor;
    }
}
