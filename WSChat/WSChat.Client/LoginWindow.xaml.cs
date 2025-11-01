using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WSChat.Shared;

namespace WSChat.Client;

public partial class LoginWindow : Window
{
    private ClientWebSocket _socket = new ClientWebSocket();
    private bool _listening = true;
    private bool _connectInProgress = false;
    private const string ServerUri = "ws://localhost:5000/chat/";

    public LoginWindow()
    {
        InitializeComponent();
    }

    private async Task EnsureConnected()
    {
        if (_socket != null && _socket.State == WebSocketState.Open) return;

        if (_connectInProgress) return;
        _connectInProgress = true;

        try
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                try { _socket?.Dispose(); } catch { }
                _socket = new ClientWebSocket();
                Console.WriteLine("[Login] Connecting to server...");
                await _socket.ConnectAsync(new Uri(ServerUri), CancellationToken.None);
                Console.WriteLine("[Login] Connected. State: " + _socket.State);
                _ = ListenServerMessages();
            }
        }
        finally
        {
            _connectInProgress = false;
        }
    }

    private async void Register_Click(object sender, RoutedEventArgs e) => await DoAuth("register");
    private async void Login_Click(object sender, RoutedEventArgs e) => await DoAuth("login");

    private async Task DoAuth(string type)
    {
        string login = LoginBox.Text.Trim();
        string password = PasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Please enter both login and password.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await EnsureConnected();

            var packet = new ChatPacket
            {
                Type = type,
                From = login,
                Text = password
            };

            string json = JsonSerializer.Serialize(packet);
            var buffer = Encoding.UTF8.GetBytes(json);
            Console.WriteLine("[Login] Sending auth packet: " + json);
            await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Auth error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Console.WriteLine("[Login] DoAuth exception: " + ex);
        }
    }

    private async Task ListenServerMessages()
    {
        var buffer = new byte[4096];
        try
        {
            while (_socket != null && _socket.State == WebSocketState.Open && _listening)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("[Login] Received: " + json);

                ChatPacket? packet = null;
                try { packet = JsonSerializer.Deserialize<ChatPacket>(json); } catch { }

                if (packet == null)
                {
                    ShowStatus("Unknown server response: " + json);
                    continue;
                }

                if (packet.Type == "login_ok" || packet.Type == "register_ok")
                {
                    var username = packet.From;
                    Console.WriteLine("[Login] Auth OK for user: " + username);
                    _listening = false;

                    Dispatcher.Invoke(() =>
                    {
                        var mainWindow = new MainWindow(_socket, username);
                        mainWindow.Show();
                        this.Close();
                    });
                    return;
                }
                else if (packet.Type == "system")
                {
                    ShowStatus(packet.Text);
                }
                else
                {
                    ShowStatus($"{packet.Type}: {packet.Text}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Login] ListenServerMessages exception: " + ex);
            MessageBox.Show($"Connection error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        });
    }
}
