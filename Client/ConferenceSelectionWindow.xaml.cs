using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Client;

public partial class ConferenceSelectionWindow : Window
{
    private Socket clientSocket;
    private string username;
    private CancellationTokenSource cts = new CancellationTokenSource();

    public ConferenceSelectionWindow(string username, Socket socket)
    {
        InitializeComponent();
        this.username = username;
        this.clientSocket = socket;
    }

    private async void CreateConference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (clientSocket != null && clientSocket.Connected)
            {
                await SendMessageAsync($"CREATE_CONFERENCE:{username}");
                string response = await ReceiveMessageAsync();
                if (response.Contains("CONFERENCE_CREATED:"))
                {
                    string conferenceId = response.Substring("CONFERENCE_CREATED:".Length);
                    OpenMainWindow(username, conferenceId);
                }
                else
                {
                    MessageBox.Show("Failed to create conference.");
                }
            }
            else
            {
                MessageBox.Show("Not connected to server.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error creating conference: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private async void SubmitConferenceId_Click(object sender, RoutedEventArgs e)
    {
        string conferenceId = ConferenceIdTextBox.Text.Trim();
        if (!int.TryParse(conferenceId, out int id))
        {
            MessageBox.Show("Please enter a valid Conference ID.");
            return;
        }

        try
        {
            Console.WriteLine($"Socket state before sending: {(clientSocket != null ? "Not null" : "Null")}, Connected: {clientSocket?.Connected}");
            if (clientSocket != null && clientSocket.Connected)
            {
                await SendMessageAsync($"JOIN_CONFERENCE:{conferenceId}:{username}");
                string response = await ReceiveMessageAsync();

                if (response.Contains("JOIN_CONFERENCE_SUCCESS"))
                {
                    OpenMainWindow(username, conferenceId);
                }
                else
                {
                    MessageBox.Show("Unable to join conference.");
                }
            }
            else
            {
                Console.WriteLine("Not connected to server.");
                MessageBox.Show("Not connected to server.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error joining conference: {ex.Message}");
            MessageBox.Show($"Error joining conference: {ex.Message}");
            clientSocket?.Close();
            clientSocket = null;
        }
    }

    private async void ScheduleConference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ScheduleCalendar.SelectedDate == null)
            {
                MessageBox.Show("Please select a date.");
                return;
            }

            string selectedHour = (HourComboBox.SelectedItem as ComboBoxItem)?.Content.ToString();
            string selectedMinute = (MinuteComboBox.SelectedItem as ComboBoxItem)?.Content.ToString();
            if (string.IsNullOrEmpty(selectedHour) || string.IsNullOrEmpty(selectedMinute))
            {
                MessageBox.Show("Please select both hour and minute.");
                return;
            }

            DateTime selectedDateTime = ScheduleCalendar.SelectedDate.Value.Date
                .AddHours(int.Parse(selectedHour))
                .AddMinutes(int.Parse(selectedMinute));

            if (selectedDateTime < DateTime.Now)
            {
                MessageBox.Show("Selected date and time cannot be in the past.");
                return;
            }

            string formattedDateTime = selectedDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            if (clientSocket != null && clientSocket.Connected)
            {
                await SendMessageAsync($"SCHEDULE_CONFERENCE:{username}:{formattedDateTime}");
                string response = await ReceiveMessageAsync();
                if (response.Contains("CONFERENCE_SCHEDULED:"))
                {
                    string conferenceId = response.Substring("CONFERENCE_SCHEDULED:".Length);
                    MessageBox.Show($"Conference scheduled successfully for {formattedDateTime}. Conference ID: {conferenceId}");
                }
                else
                {
                    MessageBox.Show("Failed to schedule conference.");
                }
            }
            else
            {
                MessageBox.Show("Not connected to server.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error scheduling conference: {ex.Message}");
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
            Console.WriteLine($"SendMessageAsync error: {ex.Message}");
            throw;
        }
    }

    private async Task<string> ReceiveMessageAsync()
    {
        try
        {
            clientSocket.ReceiveTimeout = 10000;
            byte[] buffer = new byte[1024];
            int bytesRec = await Task.Factory.FromAsync(
                clientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, null, null),
                clientSocket.EndReceive).ConfigureAwait(false);
            clientSocket.ReceiveTimeout = 0;
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRec).TrimEnd('\r', '\n');
            Console.WriteLine($"Received message: {response}");
            return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ReceiveMessageAsync error: {ex.Message}");
            throw;
        }
    }

    private void OpenMainWindow(string username, string conferenceId)
    {
        MainWindow mainWindow = new MainWindow();
        mainWindow.Show();
        mainWindow.ConnectToServer(username, clientSocket, conferenceId);
        Close();
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