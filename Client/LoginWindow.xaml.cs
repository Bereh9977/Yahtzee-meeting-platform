using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Client;

public partial class LoginWindow : Window
{
    private Socket clientSocket;
    private string pendingAction;
    private CancellationTokenSource cts = new CancellationTokenSource();

    public LoginWindow()
    {
        InitializeComponent();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        string login = LoginTextBox.Text.Trim();
        string password = PasswordBox.Password.Trim();
        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Please enter login and password.");
            return;
        }
        SendRequest("LOGIN", $"LOGIN:{login}:{password}");
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        string username = RegUsernameTextBox.Text.Trim();
        string login = RegLoginTextBox.Text.Trim();
        string password = RegPasswordBox.Password.Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Please fill in all fields.");
            return;
        }
        SendRequest("REGISTER", $"REGISTER:{username}:{login}:{password}");
    }

    private void GuestJoin_Click(object sender, RoutedEventArgs e)
    {
        string guestName = GuestNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(guestName))
        {
            MessageBox.Show("Please enter a guest name.");
            return;
        }
        SendRequest("GUEST", $"GUEST:{guestName}");
    }

    private async void SendRequest(string action, string message)
    {
        pendingAction = action;
        await ConnectToServerAsync();
        if (clientSocket != null && clientSocket.Connected)
        {
            await SendMessageAsync(message);
            await ReceiveResponseAsync();
        }
        else
        {
            MessageBox.Show("Failed to connect to server.");
        }
    }

    private async Task ConnectToServerAsync()
    {
        try
        {
            clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await Task.Factory.FromAsync(
                clientSocket.BeginConnect,
                clientSocket.EndConnect,
                "0.tcp.eu.ngrok.io",
                 19916,
                null).ConfigureAwait(false);
            Console.WriteLine("Connected to server.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection error: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private async Task SendMessageAsync(string msg)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            await Task.Factory.FromAsync(
                clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                clientSocket.EndSend).ConfigureAwait(false);
            Console.WriteLine($"Sent message: {msg}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Send error: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private async Task ReceiveResponseAsync()
    {
        try
        {
            clientSocket.ReceiveTimeout = 10000;
            string response = await ReceiveMessageAsync();
            clientSocket.ReceiveTimeout = 0;
            if (response != null)
            {
                HandleServerResponse(response);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Response error: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private async Task<string> ReceiveMessageAsync()
    {
        try
        {
            byte[] buffer = new byte[1024];
            int bytesRec = await Task.Factory.FromAsync(
                clientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, null, null),
                clientSocket.EndReceive).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRec).TrimEnd('\r', '\n');
            Console.WriteLine($"Received message: {response}");
            return response;
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"Receive error: {ex.Message}"));
            return null;
        }
    }

    private void HandleServerResponse(string response)
    {
        switch (pendingAction)
        {
            case "LOGIN":
                HandleLoginResponse(response);
                break;
            case "REGISTER":
                HandleRegisterResponse(response);
                break;
            case "GUEST":
                HandleGuestResponse(response);
                break;
        }
    }

    private void HandleLoginResponse(string response)
    {
        if (response.StartsWith("LOGIN_SUCCESS:"))
        {
            string username = response.Substring("LOGIN_SUCCESS:".Length);
            Console.WriteLine($"Login successful for {username}");
            OpenConferenceSelectionWindow(username);
        }
        else
        {
            MessageBox.Show("Invalid login or password.");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private void HandleRegisterResponse(string response)
    {
        if (response == "REGISTER_SUCCESS")
        {
            string login = RegLoginTextBox.Text.Trim();
            string password = RegPasswordBox.Password.Trim();

            RegUsernameTextBox.Text = "";
            RegLoginTextBox.Text = "";
            RegPasswordBox.Password = "";

            if (MainTabControl != null)
            {
                MainTabControl.SelectedItem = LoginTab;
            }

            LoginTextBox.Text = login;
            PasswordBox.Password = password;

            MessageBox.Show("Registration successful. Please login with your credentials.");
        }
        else
        {
            MessageBox.Show("Registration failed. Login may already exist.");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private void HandleGuestResponse(string response)
    {
        if (response == "GUEST_SUCCESS")
        {
            string username = GuestNameTextBox.Text.Trim();
            Console.WriteLine($"Guest join successful for {username}");
            OpenConferenceSelectionWindow(username);
        }
        else
        {
            MessageBox.Show("Failed to join as guest.");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private void OpenConferenceSelectionWindow(string username)
    {
        try
        {
            if (clientSocket == null || !clientSocket.Connected)
            {
                MessageBox.Show("Socket is not connected.");
                return;
            }

            ConferenceSelectionWindow selectionWindow = new ConferenceSelectionWindow(username, clientSocket);
            selectionWindow.Show();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening ConferenceSelectionWindow: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        cts.Cancel();
        if (clientSocket != null && clientSocket.Connected)
        {
            try { clientSocket.Shutdown(SocketShutdown.Both); } catch { }
            clientSocket.Close();
        }
        clientSocket = null;
    }
}