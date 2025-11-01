using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WSChat.Shared;

namespace WSChat.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket _socket;
    private readonly string _username;
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly ConcurrentQueue<ChatPacket> _outQueue = new();
    private bool _processingQueue = false;
    private readonly object _queueLock = new();

    public MainWindow(ClientWebSocket socket, string username)
    {
        InitializeComponent();

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _username = username ?? throw new ArgumentNullException(nameof(username));

        MessageList.ItemsSource = _messages;

        Console.WriteLine($"[MainWindow] ctor: socket state = {_socket.State}, username = {_username}");

        _ = ReceiveMessages();
        _ = ProcessQueueAsync();

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            try
            {
                var ping = new ChatPacket { Type = "ping", From = _username, Text = "ping" };
                string pjson = JsonSerializer.Serialize(ping);
                Console.WriteLine("[Client] Sending test ping: " + pjson);
                var buf = Encoding.UTF8.GetBytes(pjson);
                if (_socket != null && _socket.State == WebSocketState.Open)
                    await _socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Client] Ping failed: " + ex);
            }
        });
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessage();

    private async Task SendMessage()
    {
        try
        {
            string message = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            ChatPacket packet;
            if (message.StartsWith("/w "))
            {
                var parts = message.Split(' ', 3);
                if (parts.Length >= 3)
                {
                    packet = new ChatPacket { Type = "private", From = _username, To = parts[1], Text = parts[2] };
                }
                else
                {
                    _messages.Add(new ChatMessage { User = "System", Text = "Private format: /w username text", IsMine = false });
                    return;
                }
            }
            else
            {
                packet = new ChatPacket { Type = "message", From = _username, Text = message };
            }

            _outQueue.Enqueue(packet);
            _ = ProcessQueueAsync();

            InputBox.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Client] SendMessage exception: " + ex);
            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Error", Text = ex.Message, IsMine = false }));
        }
    }

    private async Task ProcessQueueAsync()
    {
        if (_processingQueue) return;
        lock (_queueLock)
        {
            if (_processingQueue) return;
            _processingQueue = true;
        }

        try
        {
            while (_outQueue.TryDequeue(out var pkt))
            {
                if (_socket == null || _socket.State != WebSocketState.Open)
                {
                    _outQueue.Enqueue(pkt);
                    await Task.Delay(300);
                    continue;
                }

                try
                {
                    string json = JsonSerializer.Serialize(pkt);
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    Console.WriteLine("[Client] Sending: " + json);
                    await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    await Task.Delay(10);
                }
                catch (Exception sx)
                {
                    Console.WriteLine("[Client] Send failed, requeue: " + sx);
                    _outQueue.Enqueue(pkt);
                    await Task.Delay(300);
                }
            }
        }
        finally
        {
            _processingQueue = false;
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SendMessage();
            e.Handled = true;
        }
    }

    private async Task ReceiveMessages()
    {
        var buffer = new byte[4096];
        try
        {
            while (_socket != null && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[Client] Server closed connection");
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("[Client] Received: " + json);

                ChatPacket? pkt = null;
                try { pkt = JsonSerializer.Deserialize<ChatPacket>(json); } catch { }

                if (pkt == null)
                {
                    Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Raw", Text = json, IsMine = false }));
                    continue;
                }

                switch (pkt.Type)
                {
                    case "system":
                        Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "System", Text = pkt.Text, IsMine = false }));
                        break;

                    case "message":
                        {
                            bool isMine = pkt.From == _username;
                            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = pkt.From, Text = pkt.Text, IsMine = isMine }));
                            break;
                        }

                    case "private":
                        {
                            if (pkt.To == _username || pkt.From == _username)
                                Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = $"(private) {pkt.From}", Text = pkt.Text, IsMine = pkt.From == _username }));
                            break;
                        }

                    default:
                        Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Unknown", Text = json, IsMine = false }));
                        break;
                }

                if (!_processingQueue)
                    _ = ProcessQueueAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Client] ReceiveMessages exception: " + ex);
            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Error", Text = ex.Message, IsMine = false }));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            if (_socket != null && _socket.State == WebSocketState.Open)
                _ = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
        }
        catch { }
    }
}
