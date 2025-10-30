using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Text.Json;
using WSChat.Shared;


namespace WSChat.Client;

public partial class MainWindow : Window
{
    private ClientWebSocket _socket = new ClientWebSocket();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _socket.ConnectAsync(new Uri("ws://localhost:5000/chat/"), CancellationToken.None);
        _ = ReceiveMessages();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendMessage();
    }

    private async void SendMessage()
    {
        string message = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message)) return;

        var msg = new ChatMessage
        {
            User = "User",
            Text = message,
            IsMine = true
        };

        MessageList.Items.Add(msg);

        string json = JsonSerializer.Serialize(msg);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);

        AddMessage(msg);
        InputBox.Clear();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private async Task ReceiveMessages()
    {
        var buffer = new byte[1024 * 4];

        while (_socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                var msg = JsonSerializer.Deserialize<ChatMessage>(json);
                if (msg != null)
                {
                    Dispatcher.Invoke(() => MessageList.Items.Add(msg));
                }
            }
            catch
            {
                Dispatcher.Invoke(() => MessageList.Items.Add(new ChatMessage
                {
                    User = "Server",
                    Text = json,
                    IsMine = false
                }));
            }
        }
    }

    private void AddMessage(ChatMessage msg)
    {
        var bubble = new Border
        {
            Background = msg.IsMine ? Brushes.LightBlue : Brushes.Gray,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(5),
            HorizontalAlignment = msg.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Children =
            {
                new TextBlock { Text = msg.User, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                new TextBlock { Text = msg.Text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }
            }
            }
        };

        MessageList.Items.Add(bubble);
        MessageList.ScrollIntoView(bubble);
    }
}
