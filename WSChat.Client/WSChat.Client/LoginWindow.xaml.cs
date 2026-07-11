using Microsoft.VisualBasic.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.IsolatedStorage;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WSChat.Shared;

namespace WSChat.Client;

public partial class LoginWindow : Window
{
    private ClientWebSocket _socket = new ClientWebSocket();
    private bool _listening = true;
    private bool _connectInProgress = false;
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private const string ServerUri = "ws://localhost:5000/chat/";
    private const string TokenFileName = "chat_remember_token.txt";
    private bool _isRegisterMode = false;

    public LoginWindow()
    {
        InitializeComponent();
        _ = TryAutoLogin();
    }

    private async Task TryAutoLogin()
    {
        var token = LoadRememberToken();
        if (!string.IsNullOrEmpty(token))
        {
            await EnsureConnected();
            Console.WriteLine($"[Login] Auto-login with token: {token}");
            var packet = new ChatPacket { Type = "login_with_token", Text = token };
            await SendPacketAsync(packet);
        }
    }

    private async Task EnsureConnected()
    {
        if (_socket.State == WebSocketState.Open) return;

        _socket?.Dispose();
        _socket = new ClientWebSocket();

        try
        {
            await _socket.ConnectAsync(new Uri(ServerUri), CancellationToken.None);
            Console.WriteLine("[Login] Connected to server.");
            _ = ListenServerMessages();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Login] Connection failed: " + ex.Message);
            MessageBox.Show("Cannot connect to server: " + ex.Message);
        }
    }

    private void ToggleRegister_Click(object sender, RoutedEventArgs e)
    {
        _isRegisterMode = !_isRegisterMode;

        if (_isRegisterMode)
        {
            ModeText.Text = "Register";
            EmailPanel.Visibility = Visibility.Visible;

            var button = (Button)sender;
            button.Content = "Back to Login";

            LoginButton.Content = "Sign Up";
        }
        else
        {
            ModeText.Text = "Login";
            EmailPanel.Visibility = Visibility.Collapsed;

            var button = (Button)sender;
            button.Content = "Register";

            LoginButton.Content = "Login";
        }

        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginBox.Text.Trim();
        string password = PasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("Please enter login and password.");
            return;
        }

        if (_isRegisterMode)
        {
            string email = EmailBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                ShowStatus("Please enter a valid email.");
                return;
            }
        }

        try
        {
            await EnsureConnected();

            var packet = new ChatPacket
            {
                Type = _isRegisterMode ? "register" : "login",
                From = login,
                Text = password
            };

            if (_isRegisterMode)
            {
                packet.Data = EmailBox.Text.Trim();
            }
            else
            {
                packet.Remember = RememberMeCheckBox.IsChecked == true;
            }

            await SendPacketAsync(packet);
        }
        catch (Exception ex)
        {
            ShowStatus($"Error: {ex.Message}");
        }
    }
    private async Task SendPacketAsync(ChatPacket pkt)
    {
        if (_socket.State != WebSocketState.Open) return;
        string json = JsonSerializer.Serialize(pkt);
        var buffer = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ListenServerMessages(CancellationToken token = default)
    {
        var buffer = new byte[4096];
        try
        {
            while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("[Login] Received: " + json);

                var packet = JsonSerializer.Deserialize<ChatPacket>(json);
                if (packet == null) continue;

                switch (packet.Type)
                {
                    case "login_ok":
                    case "register_ok":
                        var username = packet.From ?? "User";

                        _listening = false;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            this.Hide();
                            var main = new MainWindow(_socket, username);
                            main.Closed += (s, e) =>
                            {
                                try { _socket?.Abort(); } catch { }
                                _socket?.Dispose();
                                Application.Current.Shutdown();
                            };
                            main.Show();
                        });
                        return;

                    case "system":
                        ShowStatus(packet.Text);
                        break;

                    case "remember_token":
                        SaveRememberToken(packet.Text);
                        break;
                }
            }
        }
        catch (OperationCanceledException) 
        {
            Console.WriteLine("[Login] ListenServerMessages cancelled successfully.");
        }
        catch (Exception ex)
        {
            if (_listening)
                Console.WriteLine("[Login] Listen error: " + ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        });
    }

    private void SaveRememberToken(string token)
    {
        try
        {
            using (var isoStore = IsolatedStorageFile.GetUserStoreForAssembly())
            using (var isoStream = new IsolatedStorageFileStream(TokenFileName, FileMode.Create, isoStore))
            using (var writer = new StreamWriter(isoStream))
            {
                writer.Write(token);
            }
            Console.WriteLine($"[Login] Token saved to {TokenFileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Login] Save token error: " + ex.Message);
        }
    }

    private string LoadRememberToken()
    {
        try
        {
            using (var isoStore = IsolatedStorageFile.GetUserStoreForAssembly())
            {
                if (!isoStore.FileExists(TokenFileName)) return string.Empty;

                using (var isoStream = new IsolatedStorageFileStream(TokenFileName, FileMode.Open, isoStore))
                using (var reader = new StreamReader(isoStream))
                {
                    return reader.ReadToEnd().Trim();
                }
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}