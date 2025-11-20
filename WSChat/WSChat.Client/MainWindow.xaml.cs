using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WSChat.Shared;
using System.Windows.Media;

namespace WSChat.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket _socket;
    private readonly string _username;
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly ObservableCollection<string> _rooms = new();
    private readonly ConcurrentQueue<ChatPacket> _outQueue = new();
    private bool _processingQueue = false;
    private readonly object _queueLock = new();
    private string _currentRoom = "General";
    private AdminWindow? _adminWindow;

    public MainWindow(ClientWebSocket socket, string username)
    {
        InitializeComponent();

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _username = username ?? throw new ArgumentNullException(nameof(username));

        MessageList.ItemsSource = _messages;
        RoomsCombo.ItemsSource = _rooms;
        CurrentRoomText.Text = $"Current: {_currentRoom}";
         
        if (_username == "admin")
        {
            AdminButton.Visibility = Visibility.Visible;
        }

        _ = ReceiveMessages();
        _ = ProcessQueueAsync();

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await SendPacketAsync(new ChatPacket { Type = "list_rooms", From = _username });
        });
    }

    private async Task SendPacketAsync(ChatPacket pkt)
    {
        _outQueue.Enqueue(pkt);
        _ = ProcessQueueAsync();
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
                    await Task.Delay(5);
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

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessage();

    private async Task SendMessage()
    {
        try
        {
            string message = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            ChatPacket packet;

            if (message.StartsWith("/join "))
            {
                string room = message.Substring(6).Trim();
                if (!string.IsNullOrEmpty(room))
                    packet = new ChatPacket { Type = "join", From = _username, Text = room };
                else return;
            }
            else if (message.StartsWith("/w "))
            {
                var parts = message.Split(' ', 3);
                if (parts.Length < 3)
                {
                    _messages.Add(new ChatMessage { User = "System", Text = "Usage: /w username text", IsMine = false });
                    return;
                }
                packet = new ChatPacket { Type = "private", From = _username, To = parts[1], Text = parts[2] };
            }
            else if (message.StartsWith("/kick "))
            {
                packet = new ChatPacket { Type = "kick", From = _username, To = message.Substring(6).Trim() };
            }
            else if (message.StartsWith("/ban "))
            {
                packet = new ChatPacket { Type = "ban", From = _username, To = message.Substring(5).Trim() };
            }
            else
            {
                packet = new ChatPacket { Type = "chat_message", From = _username, Text = message };
            }

            await SendPacketAsync(packet);
            InputBox.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Client] SendMessage exception: " + ex);
            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Error", Text = ex.Message, IsMine = false }));
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
                        if (pkt.Text != null && pkt.Text.StartsWith("You have joined room:"))
                        {
                            var room = pkt.Text.Substring("You have joined room:".Length).Trim();
                            _currentRoom = room;
                            Dispatcher.Invoke(() => CurrentRoomText.Text = $"Current: {_currentRoom}");
                        }
                        break;

                    case "rooms":
                        {
                            var roomsText = pkt.Text ?? "";
                            var arr = roomsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                            Dispatcher.Invoke(() =>
                            {
                                _rooms.Clear();
                                foreach (var r in arr) _rooms.Add(r);
                                if (RoomsCombo.SelectedItem == null && _rooms.Count > 0)
                                {
                                    RoomsCombo.SelectedIndex = _rooms.IndexOf(_currentRoom);
                                    if (RoomsCombo.SelectedIndex < 0) RoomsCombo.SelectedIndex = 0;
                                }
                            });
                            break;
                        }

                    case "who":
                        {
                            var who = pkt.Text ?? "";
                            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Who", Text = who, IsMine = false }));
                            break;
                        }

                    case "message":
                        {
                            bool isMine = pkt.From == _username;
                            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = pkt.From, Text = pkt.Text, IsMine = isMine }));
                            break;
                        }

                    case "censor_warning":
                        if (_adminWindow != null && pkt.From != null && pkt.Text != null)
                        {
                            Dispatcher.Invoke(() => _adminWindow.AddCensorWarning($"{pkt.From}: {pkt.Text}"));
                        }
                        break;

                    case "private":
                        {
                            if (pkt.To == _username || pkt.From == _username)
                                Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = $"(private) {pkt.From}", Text = pkt.Text, IsMine = pkt.From == _username }));
                            break;
                        }

                    case "admin_list":
                        {
                            var usersText = pkt.Text ?? "";
                            var users = usersText
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .ToArray();

                            if (_adminWindow != null)
                            {
                                Dispatcher.Invoke(() => _adminWindow.SetUsers(users));
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    _messages.Add(new ChatMessage
                                    {
                                        User = "Admin",
                                        Text = "Active users: " + string.Join("\n", users),
                                        IsMine = false
                                    });
                                });
                            }
                        }
                        break;

                    default:
                        Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = pkt.From ?? "Server", Text = pkt.Text ?? "", IsMine = false }));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Client] ReceiveMessages exception: " + ex);
            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = "Error", Text = ex.Message, IsMine = false }));
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var name = CreateRoomBox.Text.Trim();
        var tip = CreateRoomBox.Tag as string ?? "";
        if (name == tip) name = "";
        if (string.IsNullOrEmpty(name)) return;
        await SendPacketAsync(new ChatPacket { Type = "create", From = _username, Text = name });
        CreateRoomBox.Text = tip;
        CreateRoomBox.Foreground = Brushes.Gray;
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (RoomsCombo.SelectedItem is string room)
        {
            await SendPacketAsync(new ChatPacket { Type = "join", From = _username, Text = room });
        }
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SendPacketAsync(new ChatPacket { Type = "leave", From = _username });
    }

    private async void WhoButton_Click(object sender, RoutedEventArgs e)
    {
        await SendPacketAsync(new ChatPacket { Type = "who", From = _username });
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                MessageBox.Show("Socket not connected. Cannot open Admin window.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_adminWindow == null)
            {
                _adminWindow = new AdminWindow(_socket, _username);
                _adminWindow.Owner = this;
                _adminWindow.Closed += (_, __) => _adminWindow = null;

                _adminWindow.Show(); 
            }
            else
            {
                _adminWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to open Admin window: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void CreateRoomBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            var tip = tb.Tag as string ?? "";
            if (tb.Text == tip)
            {
                tb.Text = "";
                tb.Foreground = Brushes.Black;
            }
        }
    }

    private void CreateRoomBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                tb.Text = tb.Tag as string ?? "";
                tb.Foreground = Brushes.Gray;
            }
        }
    }
}
