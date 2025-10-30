using System.Net;
using System.Net.WebSockets;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using WSChat.Shared;

namespace WSChat.Server;

public class WSChatServer
{
    private static readonly List<WebSocket> Clients = new();

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
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"Received: {message}");

                string jsonMessage = JsonSerializer.Serialize(new ChatMessage
                { 
                    User = "User",
                    Text = message,
                    IsMine = false
                });

                await BroadcastMessage(jsonMessage);
            }
        }
        finally
        {
            lock (Clients) { Clients.Remove(socket); }
            socket.Dispose();

            string disconnectMessage = JsonSerializer.Serialize(new ChatMessage
            {
                User = "Server",
                Text = "Client disconnected",
                IsMine = false
            });
            await BroadcastMessage(disconnectMessage);

            Console.WriteLine("Client disconnected");
        }
    }

    private static async Task BroadcastMessage(string json)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        lock (Clients)
        {
            foreach (var client in Clients.ToList())
            {
                if (client.State == WebSocketState.Open)
                {
                    _ = client.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
    }
}

