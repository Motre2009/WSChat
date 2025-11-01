using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WSChat.Shared;

namespace WSChat.Server;

public class WSChatServer
{
    private static readonly List<WebSocket> Clients = new();
    private static readonly Dictionary<WebSocket, string> UserNames = new();
    private static readonly Dictionary<string, string> RegisteredUsers = new();

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
                lock (Clients) { Clients.Add(socket); }
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

                            await SendJson(socket, new ChatPacket { Type = "register_ok", From = packet.From, Text = "Registration successful!" });

                            await BroadcastSystem($"{packet.From} joined the chat");
                        }
                        break;

                    case "login":
                        if (RegisteredUsers.TryGetValue(packet.From, out var storedHash) &&
                            BCrypt.Net.BCrypt.Verify(packet.Text, storedHash))
                        {
                            lock (UserNames) { UserNames[socket] = packet.From; }
                            connectedUser = packet.From;

                            await SendJson(socket, new ChatPacket { Type = "login_ok", From = packet.From, Text = "Login successful!" });

                            await BroadcastSystem($"{packet.From} joined the chat");
                        }
                        else
                        {
                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "Invalid login or password." });
                        }
                        break;

                    case "message":
                        if (UserNames.TryGetValue(socket, out var userName))
                        {
                            await BroadcastMessage(userName, packet.Text);
                        }
                        else
                        {
                            await SendJson(socket, new ChatPacket { Type = "system", From = "server", Text = "You are not authenticated." });
                        }
                        break;

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
            lock (Clients) { Clients.Remove(socket); }

            if (connectedUser != null)
            {
                lock (UserNames) { UserNames.Remove(socket); }
                await BroadcastSystem($"{connectedUser} disconnected");
            }

            try { socket.Dispose(); } catch { }
            Console.WriteLine("Client disconnected");
        }
    }

    private static async Task BroadcastSystem(string text)
    {
        var pkt = new ChatPacket { Type = "system", From = "Server", Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Broadcasting SYSTEM: {json}");
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        lock (Clients)
        {
            foreach (var client in Clients.ToList())
            {
                if (client.State == WebSocketState.Open)
                {
                    _ = client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
    }

    private static async Task BroadcastMessage(string from, string text)
    {
        var pkt = new ChatPacket { Type = "message", From = from, Text = text };
        string json = JsonSerializer.Serialize(pkt);
        Console.WriteLine($"[Server] >>> Broadcasting MESSAGE: {json}");
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        lock (Clients)
        {
            foreach (var client in Clients.ToList())
            {
                if (client.State == WebSocketState.Open)
                {
                    _ = client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
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
}
