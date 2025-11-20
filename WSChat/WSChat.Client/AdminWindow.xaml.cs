using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WSChat.Shared;

namespace WSChat.Client;

public partial class AdminWindow : Window
{
    private readonly ClientWebSocket _socket;
    private readonly string _adminName;
    private List<string> _allUsers = new();

    public AdminWindow(ClientWebSocket socket, string adminName)
    {
        InitializeComponent();

        if (socket == null)
        {
            MessageBox.Show("Socket is null");
        }
        else if (socket.State != WebSocketState.Open)
        {
            MessageBox.Show("Socket is not open: " + socket.State);
        }

        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _adminName = adminName ?? throw new ArgumentNullException(nameof(adminName));

        Loaded += async (_, __) => await RefreshUsersSafe();
        Closed += (_, __) => { };
    }

    private async Task RefreshUsersSafe()
    {
        if (_socket == null || _socket.State != WebSocketState.Open)
        {
            MessageBox.Show("Socket not connected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SafeSend(new ChatPacket
        {
            Type = "admin_list",
            From = _adminName,
            Text = ""
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUsersSafe();
        await Task.Delay(500);
    }


    private void SafeSend(ChatPacket pkt)
    {
        Task.Run(async () =>
        {
            try
            {
                if (_socket.State != WebSocketState.Open)
                    return;

                string json = JsonSerializer.Serialize(pkt);
                var buffer = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show("Failed to send: " + ex.Message));
            }
        });
    }

    private string GetSelectedUser() => UsersList.SelectedItem?.ToString() ?? "";

    private async void BanButton_Click(object sender, RoutedEventArgs e)
    {
        var user = GetSelectedUser();
        if (string.IsNullOrEmpty(user)) return;

        if (!int.TryParse(BanMinutesBox.Text.Trim(), out int minutes))
            minutes = 60;

        BanBtn.IsEnabled = DeleteBtn.IsEnabled = false;
        try
        {
            SafeSend(new ChatPacket
            {
                Type = "admin_ban",
                From = _adminName,
                To = user,
                Text = minutes.ToString()
            });

            await RefreshUsersSafe();
            await Task.Delay(500);
        }
        finally
        {
            BanBtn.IsEnabled = DeleteBtn.IsEnabled = true;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var user = GetSelectedUser();
        if (string.IsNullOrEmpty(user)) return;

        BanBtn.IsEnabled = DeleteBtn.IsEnabled = false;
        try
        {
            SafeSend(new ChatPacket
            {
                Type = "admin_delete",
                From = _adminName,
                To = user,
                Text = ""
            });

            await RefreshUsersSafe();
            await Task.Delay(500);
        }
        finally
        {
            BanBtn.IsEnabled = DeleteBtn.IsEnabled = true;
        }
    }

    public void SetUsers(string[] users)
    {
        Dispatcher.Invoke(() =>
        {
            _allUsers = users.ToList();
            UsersList.ItemsSource = _allUsers;
        });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || UsersList == null) return;

        var query = SearchBox.Text.Trim().ToLowerInvariant();
        if (query == "search...") query = "";

        var filtered = string.IsNullOrEmpty(query)
            ? _allUsers
            : _allUsers.Where(u => u.ToLowerInvariant().Contains(query)).ToList();

        UsersList.ItemsSource = filtered;
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text == "Search...")
        {
            SearchBox.Text = "";
            SearchBox.Foreground = Brushes.White;
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchBox.Text = "Search...";
            SearchBox.Foreground = Brushes.Gray;
        }
    }
}
