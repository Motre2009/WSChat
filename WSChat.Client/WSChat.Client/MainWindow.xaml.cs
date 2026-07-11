using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WSChat.Shared;

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
    private bool _isRecording = false;
    private WaveInEvent? _waveIn;
    private UdpClient? _udpClient;
    private readonly int _udpPort = 5002;
    private IPEndPoint? _serverEndPoint;
    private MemoryStream? _voiceStream;
    private WaveFileWriter? _waveWriter;
    private string? _currentVoiceTarget = null;

    public MainWindow(ClientWebSocket socket, string username)
    {
        InitializeComponent();

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _username = username ?? throw new ArgumentNullException(nameof(username));

        MessageList.ItemsSource = _messages;
        RoomsCombo.ItemsSource = _rooms;
         
        if (_username == "admin")
        {
            AdminButton.Visibility = Visibility.Visible;
        }

        _serverEndPoint = new IPEndPoint(IPAddress.Loopback, _udpPort);
        _udpClient = new UdpClient(0);
        var localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
        _ = Task.Run(UdpReceiveLoop);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            await SendPacketAsync(new ChatPacket
            {
                Type = "udp_register",
                From = _username,
                Text = localPort.ToString()
            });
        });

        Console.WriteLine("[Client] UDP local port: " + localPort);
        _ = ReceiveMessages();
        _ = ProcessQueueAsync();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _socket?.Dispose();
        _udpClient?.Close();
        _udpClient?.Dispose();
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

            if (message.StartsWith("/register "))
            {
                var parts = message.Split(' ', 3);
                if (parts.Length < 3)
                {
                    _messages.Add(new ChatMessage { User = "System", Text = "Usage: /register username password", IsMine = false });
                    return;
                }

                string username = parts[1];
                string password = parts[2];

                string email = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter your email for register:",
                    "Register",
                    "example@gmail.com");

                if (string.IsNullOrWhiteSpace(email))
                {
                    _messages.Add(new ChatMessage { User = "System", Text = "Реєстрація скасована (email не введено)", IsMine = false });
                    return;
                }

                var registerPacket = new ChatPacket
                {
                    Type = "register",
                    From = username,
                    To = email,
                    Text = password
                };

                await SendPacketAsync(registerPacket);
                _messages.Add(new ChatMessage { User = "System", Text = $"Спроба реєстрації {username} з email {email}...", IsMine = false });
                InputBox.Clear();
                return;
            }
            else if (message.StartsWith("/join "))
            {
                string room = message.Substring(6).Trim();
                if (!string.IsNullOrEmpty(room))
                    packet = new ChatPacket { Type = "join", From = _username, Text = room };
                else return;
            }
            else if (message.StartsWith("/pm "))
            {
                var parts = message.Split(' ', 3);
                if (parts.Length < 3)
                {
                    _messages.Add(new ChatMessage { User = "System", Text = "Usage: /pm username text", IsMine = false });
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
            else if (message.StartsWith("/weather "))
            {
                var city = message.Substring(9).Trim();
                packet = new ChatPacket { Type = "weather", From = _username, Text = city };
            }
            else if (message.StartsWith("/news"))
            {
                var parts = message.Split(' ', 2);
                var category = parts.Length > 1 ? parts[1].Trim() : "general";
                packet = new ChatPacket { Type = "news", From = _username, Text = category };
            }
            else if (message.StartsWith("/joke"))
            {
                packet = new ChatPacket { Type = "joke", From = _username, Text = "" };
            }
            else if (message.StartsWith("/programming"))
            {
                packet = new ChatPacket { Type = "programming", From = _username, Text = "" };
            }
            else if (message.StartsWith("/quote"))
            {
                packet = new ChatPacket { Type = "quote", From = _username, Text = "" };
            }
            else if (message.StartsWith("/reset "))
            {
                var emailOrUsername = message.Substring(7).Trim();
                packet = new ChatPacket { Type = "reset_password", From = _username, Text = emailOrUsername };
            }
            else if (message.StartsWith("/email "))
            {
                packet = new ChatPacket { Type = "send_email", From = _username, Text = message.Substring(7).Trim() };
            }
            else
            {
                packet = new ChatPacket { Type = "message", From = _username, Text = message };
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
                            Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = pkt.From, Text = pkt.Text, IsMine = isMine, Timestamp = pkt.Timestamp }));
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
                                Dispatcher.Invoke(() => _messages.Add(new ChatMessage { User = $"(private) {pkt.From}", Text = pkt.Text, IsMine = pkt.From == _username, Timestamp = pkt.Timestamp }));
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
        finally
        {
            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null!; 
            }

            Dispatcher.Invoke(() =>
            {
                _messages.Add(new ChatMessage { User = "System", Text = "Disconnected from server.", IsMine = false });
            });
        }
    }

    private async Task UdpReceiveLoop()
    {
        Console.WriteLine("[Client] UDP receive loop started");

        while (true)
        {
            try
            {
                if (_udpClient == null) return;

                var result = await _udpClient.ReceiveAsync();
                var json = Encoding.UTF8.GetString(result.Buffer);
                Console.WriteLine($"[Client] UDP received {result.Buffer.Length} bytes from {result.RemoteEndPoint}");

                var packet = JsonSerializer.Deserialize<VoicePacket>(json);

                if (packet == null || packet.To != _username)
                {
                    Console.WriteLine($"[Client] UDP packet not for me (I'm {_username}, target: {packet?.To})");
                    continue;
                }

                Console.WriteLine($"[Client] Playing voice from {packet.From}, size: {packet.Data?.Length ?? 0} bytes");

                Dispatcher.Invoke(() =>
                {
                    _messages.Add(new ChatMessage
                    {
                        User = $"(Voice from {packet.From})",
                        Text = "[Voice message]",
                        IsMine = false
                    });

                    if (packet.Data != null && packet.Data.Length > 0)
                    {
                        PlayVoice(packet.Data);
                    }
                    else
                    {
                        Console.WriteLine("[Client] Voice data is empty!");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] UDP receive error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }

    private void PlayVoice(byte[]? data)
    {
        if (data == null || data.Length == 0) return;

        try
        {
            using var ms = new MemoryStream(data);
            using var rdr = new WaveFileReader(ms);

            var volumeSampleProvider = new VolumeSampleProvider(rdr.ToSampleProvider())
            {
                Volume = 5.0f 
            };

            using var waveOut = new WaveOutEvent();
            waveOut.Init(volumeSampleProvider);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing)
                Task.Delay(100).Wait();

            Console.WriteLine($"[Voice] Played {data.Length} bytes");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Play error: " + ex.Message);
            MessageBox.Show("Could not play voice: " + ex.Message);
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

    private async void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = new
            {
                Title = $"Chat Statistics - {DateTime.Now:yyyy-MM-dd HH-mm}",
                Period = "last2days"
            };

            using var client = new HttpClient();
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("http://localhost:5000/reports/generate", content);
            if (response.IsSuccessStatusCode)
            {
                var pdf = await response.Content.ReadAsByteArrayAsync();
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Chat_Report_{DateTime.Now:yyyy-MM-dd_HH-mm}.pdf"
                );
                await File.WriteAllBytesAsync(path, pdf);
                MessageBox.Show($"Report saved at desktop:\n{path}", "Read!",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Exception: " + ex.Message);
        }
    }

    private void VoiceButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRecording) return;

        var users = _messages
            .Where(m => m.User.StartsWith("(private) "))
            .Select(m => m.User.Replace("(private) ", ""))
            .Distinct()
            .ToList();

        if (!users.Any())
        {
            MessageBox.Show("First type /w user hello", "No interlocutor");
            return;
        }

        _currentVoiceTarget = users.Last();

        try
        {
            _voiceStream = new MemoryStream();
            _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
            _waveWriter = new WaveFileWriter(new IgnoreDisposeStream(_voiceStream), _waveIn.WaveFormat);

            _waveIn.DataAvailable += (s, a) => _waveWriter?.Write(a.Buffer, 0, a.BytesRecorded);
            _waveIn.StartRecording();

            _isRecording = true;

            VoiceButton.Text = "REC";
            VoiceButton.Background = new SolidColorBrush(Colors.DarkRed);
            VoiceButton.FontSize = 28;

            Console.WriteLine("[Voice] Recording started");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Microphone error: " + ex.Message);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isRecording)
        {
            _isRecording = false;
            _ = StopRecordingAndSend();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_isRecording)
        {
            _isRecording = false;
            _ = StopRecordingAndSend();
        }
    }

    private async Task StopRecordingAndSend()
    {
        if (_waveIn == null) return;
        _waveIn.StopRecording();

        WaveFormat format = _waveIn.WaveFormat;

        _waveIn.Dispose();
        _waveIn = null;

        VoiceButton.Text = "MIC";
        VoiceButton.Background = new SolidColorBrush(Color.FromRgb(0, 102, 204));
        VoiceButton.FontSize = 36;

        if (_voiceStream == null || _voiceStream.Length < 2000)
        {
            Console.WriteLine("[Voice] Voice too short: " + (_voiceStream?.Length ?? 0));
            _voiceStream?.Dispose();
            _voiceStream = null;
            return;
        }

        Console.WriteLine("[Voice] Voice length: " + _voiceStream.Length + " — sending...");

        byte[] rawData = _voiceStream.ToArray();
        _voiceStream.Dispose();
        _voiceStream = null;

        byte[] wavData;
        using (var ms = new MemoryStream())
        {
            using var writer = new WaveFileWriter(ms, format);
            int maxLenth = Math.Min(rawData.Length, 16000);
            writer.Write(rawData, 0, rawData.Length);
            writer.Flush();
            wavData = ms.ToArray();
        }

        var base64Data = Convert.ToBase64String(wavData);

        var packet = new
        {
            From = _username,
            To = _currentVoiceTarget,
            Data = base64Data,
            Timestamp = DateTime.UtcNow
        };

        if (wavData.Length > 60000)
        {
            MessageBox.Show("Voice message too long for UDP. Please record a shorter message.");
            return;
        }

        var json = JsonSerializer.Serialize(packet);
        if (json.Length > 65000)
        {
            MessageBox.Show("Voice message too large to send");
            return;
        }

        var data = Encoding.UTF8.GetBytes(json);

        Console.WriteLine("[Voice] Trying to send " + wavData.Length + " bytes (with WAV header)");

        try
        {
            await _udpClient.SendAsync(data, data.Length, "127.0.0.1", 5002);
            Console.WriteLine($"[Voice] Sent {wavData.Length} bytes to {_currentVoiceTarget}");
        }
        catch (SocketException socketEx)
        {
            MessageBox.Show($"Socket error: {socketEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Send error: " + ex.Message);
        }

        Dispatcher.Invoke(() =>
    {
        _messages.Add(new ChatMessage
        {
            User = $"Voice to {_currentVoiceTarget}",
            Text = $"[Voice Message - {wavData.Length} bytes]",
            IsMine = true,
            Timestamp = DateTime.Now
        });
    });
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
