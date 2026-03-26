using Microsoft.EntityFrameworkCore;
using Server.Models;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.Collections.Generic;

namespace Server;

public partial class MainWindow : Window
{
    private Socket listenerSocket;
    private List<Socket> clients = new List<Socket>();
    private bool isRunning = false;
    private int? userId;
    private int? guestId;
    private int conferenceId;
    private Dictionary<int, Socket> clientSockets = new Dictionary<int, Socket>();
    private CancellationTokenSource cts = new CancellationTokenSource();
    private List<(int participantId, string senderName, string fileName, byte[] fileData)> sharedFiles = new List<(int, string, string, byte[])>(); // Updated to store senderName

    public MainWindow()
    {
        InitializeComponent();
        // Delete old inactive conferences on startup
        DatabaseService.DeleteOldInactiveConferences();
    }

    private void Btn_start_server_Click(object sender, RoutedEventArgs e)
    {
        if (!isRunning)
        {
            isRunning = true;
            _ = StartServerAsync(cts.Token);

            textBlock1.Text = "Сервер запущено. Очікування клієнтів...";
            btn_start_server.IsEnabled = false;
            btn_start_server.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x6D, 0x6D, 0x6D));
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenerSocket.Bind(new IPEndPoint(IPAddress.Any, 9999));
            listenerSocket.Listen(10);

            while (isRunning && !cancellationToken.IsCancellationRequested)
            {
                Socket client = await Task.Factory.FromAsync(
                    listenerSocket.BeginAccept,
                    listenerSocket.EndAccept,
                    null).ConfigureAwait(false);

                clients.Add(client);

                string clientIP = ((IPEndPoint)client.RemoteEndPoint).Address.ToString();
                UpdateMessage($"Клієнт підключився: {clientIP}");

                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                UpdateMessage($"Помилка сервера: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[25165824];
            while (isRunning && !cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await Task.Factory.FromAsync(
                    client.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, null, null),
                    client.EndReceive).ConfigureAwait(false);

                if (bytesRead == 0) break;

                string headerCheck = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 7));
                if (headerCheck.StartsWith("AUDIO:"))
                {
                    await HandleAudioMessageAsync(buffer, bytesRead, cancellationToken);
                }
                else if (headerCheck.StartsWith("VIDEO:"))
                {
                    await HandleVideoMessageAsync(buffer, bytesRead, cancellationToken);
                }
                else if (headerCheck.StartsWith("SCREEN:"))
                {
                    await HandleScreenMessageAsync(buffer, bytesRead, cancellationToken);
                }
                else if (headerCheck.StartsWith("FILE:"))
                {
                    await HandleFileMessageAsync(buffer, bytesRead, cancellationToken);
                }
                else
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    string[] messages = message.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var msg in messages)
                    {
                        Console.WriteLine($"Received message: {msg}");
                        if (msg.StartsWith("REGISTER:"))
                        {
                            await HandleRegisterAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("LOGIN:"))
                        {
                            await HandleLoginAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("GUEST:"))
                        {
                            await HandleGuestAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("CREATE_CONFERENCE:"))
                        {
                            await HandleCreateConferenceAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("SCHEDULE_CONFERENCE:"))
                        {
                            await HandleScheduleConferenceAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("JOIN_CONFERENCE:"))
                        {
                            await HandleJoinConferenceAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("CHAT_ALL:"))
                        {
                            await HandleChatAsync(msg, cancellationToken);
                        }
                        else if (msg.StartsWith("CHAT:"))
                        {
                            await HandlePrivateChatAsync(msg, cancellationToken);
                        }
                        else if (msg.StartsWith("GET_PARTICIPANTS"))
                        {
                            await HandleGetParticipantsAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("GET_CONFERENCE_ID"))
                        {
                            await HandleGetConferenceIDAsync(client, cancellationToken);
                        }
                        else if (msg.StartsWith("GET_MESSAGES:"))
                        {
                            await HandleMessagesAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("LEAVE_CONFERENCE:"))
                        {
                            await HandleLeaveConferenceAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("CAMERA_OFF:"))
                        {
                            await HandleCameraOffAsync(client, msg, cancellationToken);
                        }
                        else if (msg.StartsWith("SCREEN_OFF:"))
                        {
                            await HandleScreenOffAsync(client, msg, cancellationToken);
                        }
                        else
                        {
                            Console.WriteLine($"Sent: USER_JOINED:{msg}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HandleClient error: {ex.Message}");
            clients.Remove(client);
            var key = clientSockets.FirstOrDefault(x => x.Value == client).Key;
            if (key != 0)
            {
                clientSockets.Remove(key);
            }
        }
        finally
        {
            if (client != null && client.Connected)
            {
                try { client.Shutdown(SocketShutdown.Both); } catch { }
                client.Close();
            }
        }
    }

    private async Task HandleFileMessageAsync(byte[] buffer, int bytesRead, CancellationToken cancellationToken)
    {
        int headerEnd = -1;
        for (int i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                headerEnd = i;
                break;
            }
        }
        int senderIdEnd = -1;
        for (int i = headerEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                senderIdEnd = i;
                break;
            }
        }
        int senderNameEnd = -1;
        for (int i = senderIdEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                senderNameEnd = i;
                break;
            }
        }
        int fileNameEnd = -1;
        for (int i = senderNameEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                fileNameEnd = i;
                break;
            }
        }

        if (fileNameEnd > senderNameEnd && int.TryParse(Encoding.UTF8.GetString(buffer, headerEnd + 1, senderIdEnd - headerEnd - 1), out int senderId))
        {
            string senderName = Encoding.UTF8.GetString(buffer, senderIdEnd + 1, senderNameEnd - senderIdEnd - 1);
            string fileName = Encoding.UTF8.GetString(buffer, senderNameEnd + 1, fileNameEnd - senderNameEnd - 1);
            int fileStart = fileNameEnd + 1;
            byte[] fileData = new byte[bytesRead - fileStart];
            Buffer.BlockCopy(buffer, fileStart, fileData, 0, fileData.Length);

            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();
                if (confId != 0)
                {
                    Console.WriteLine($"Received file {fileName} from participant {senderId} ({senderName}), size: {fileData.Length} bytes");
                    sharedFiles.Add((senderId, senderName, fileName, fileData));
                    await BroadcastFileAsync(buffer, bytesRead, senderId, confId, cancellationToken);
                    UpdateMessage($"Отримано файл {fileName} від учасника {senderName}");
                }
                else
                {
                    Console.WriteLine($"No conference found for participant {senderId}");
                }
            }
        }
        else
        {
            Console.WriteLine($"Invalid file message format, size: {bytesRead} bytes");
        }
    }

    private async Task BroadcastFileAsync(byte[] fileData, int length, int senderId, int conferenceId, CancellationToken cancellationToken)
    {
        using (var context = new MeetingsContext())
        {
            var participantIds = context.ConferenceParticipants
                .Where(cp => cp.ConferenceId == conferenceId)
                .Select(cp => cp.Id)
                .ToList();

            foreach (var participantId in participantIds)
            {
                if (participantId != senderId && clientSockets.TryGetValue(participantId, out var client))
                {
                    try
                    {
                        Console.WriteLine($"Broadcasting file to participant {participantId}, size: {length} bytes");
                        await Task.Factory.FromAsync(
                            client.BeginSend(fileData, 0, length, SocketFlags.None, null, null),
                            client.EndSend).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"BroadcastFileAsync error for participant {participantId}: {ex.Message}");
                        clients.Remove(client);
                        clientSockets.Remove(participantId);
                    }
                }
            }
        }
    }

    private async Task HandleScreenMessageAsync(byte[] buffer, int bytesRead, CancellationToken cancellationToken)
    {
        int headerEnd = -1;
        for (int i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                headerEnd = i;
                break;
            }
        }
        int senderIdEnd = -1;
        for (int i = headerEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                senderIdEnd = i;
                break;
            }
        }

        if (senderIdEnd > headerEnd && int.TryParse(Encoding.UTF8.GetString(buffer, headerEnd + 1, senderIdEnd - headerEnd - 1), out int senderId))
        {
            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();
                if (confId != 0)
                {
                    Console.WriteLine($"Received screen frame from participant {senderId}, size: {bytesRead} bytes");
                    await BroadcastScreenAsync(buffer, bytesRead, senderId, confId, cancellationToken);
                    UpdateMessage($"Отримано демонстрацію екрана від учасника {senderId}");
                }
                else
                {
                    Console.WriteLine($"No conference found for participant {senderId}");
                }
            }
        }
        else
        {
            Console.WriteLine($"Invalid screen message format, size: {bytesRead} bytes");
        }
    }

    private async Task HandleScreenOffAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string participantIdStr = message.Substring("SCREEN_OFF:".Length);
        if (int.TryParse(participantIdStr, out int participantId))
        {
            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == participantId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();

                if (confId != 0)
                {
                    await BroadcastAsync($"SCREEN_OFF:{participantId}", confId, cancellationToken);
                    Console.WriteLine($"Broadcast: SCREEN_OFF for participant {participantId}");
                }
            }
        }
    }

    private async Task BroadcastScreenAsync(byte[] screenData, int length, int senderId, int conferenceId, CancellationToken cancellationToken)
    {
        using (var context = new MeetingsContext())
        {
            var participantIds = context.ConferenceParticipants
                .Where(cp => cp.ConferenceId == conferenceId)
                .Select(cp => cp.Id)
                .ToList();

            foreach (var participantId in participantIds)
            {
                if (participantId != senderId && clientSockets.TryGetValue(participantId, out var client))
                {
                    try
                    {
                        Console.WriteLine($"Broadcasting screen frame to participant {participantId}, size: {length} bytes");
                        await Task.Factory.FromAsync(
                            client.BeginSend(screenData, 0, length, SocketFlags.None, null, null),
                            client.EndSend).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"BroadcastScreenAsync error for participant {participantId}: {ex.Message}");
                        clients.Remove(client);
                        clientSockets.Remove(participantId);
                    }
                }
            }
        }
    }

    private async Task HandleVideoMessageAsync(byte[] buffer, int bytesRead, CancellationToken cancellationToken)
    {
        int headerEnd = -1;
        for (int i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                headerEnd = i;
                break;
            }
        }
        int senderIdEnd = -1;
        for (int i = headerEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                senderIdEnd = i;
                break;
            }
        }

        if (senderIdEnd > headerEnd && int.TryParse(Encoding.UTF8.GetString(buffer, headerEnd + 1, senderIdEnd - headerEnd - 1), out int senderId))
        {
            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();
                if (confId != 0)
                {
                    Console.WriteLine($"Received video from participant {senderId}, size: {bytesRead} bytes");
                    await BroadcastVideoAsync(buffer, bytesRead, senderId, confId, cancellationToken);
                    UpdateMessage($"Отримано відео від учасника {senderId}");
                }
                else
                {
                    Console.WriteLine($"No conference found for participant {senderId}");
                }
            }
        }
        else
        {
            Console.WriteLine($"Invalid video message format, size: {bytesRead} bytes");
        }
    }

    private async Task BroadcastVideoAsync(byte[] videoData, int length, int senderId, int conferenceId, CancellationToken cancellationToken)
    {
        using (var context = new MeetingsContext())
        {
            var participantIds = context.ConferenceParticipants
                .Where(cp => cp.ConferenceId == conferenceId)
                .Select(cp => cp.Id)
                .ToList();

            foreach (var participantId in participantIds)
            {
                if (participantId != senderId && clientSockets.TryGetValue(participantId, out var client))
                {
                    try
                    {
                        Console.WriteLine($"Broadcasting video to participant {participantId}, size: {length} bytes");
                        await Task.Factory.FromAsync(
                            client.BeginSend(videoData, 0, length, SocketFlags.None, null, null),
                            client.EndSend).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"BroadcastVideoAsync error for participant {participantId}: {ex.Message}");
                        clients.Remove(client);
                        clientSockets.Remove(participantId);
                    }
                }
            }
        }
    }

    private async Task HandleCameraOffAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string[] parts = message.Substring("CAMERA_OFF:".Length).Split(':');
        if (parts.Length >= 1 && int.TryParse(parts[0], out int participantId))
        {
            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == participantId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();

                if (confId != 0)
                {
                    var username = context.ConferenceParticipants
                        .Where(cp => cp.Id == participantId)
                        .Select(cp => cp.User != null ? cp.User.UserName : cp.Guest.GuestName)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(username))
                    {
                        string broadcastMessage = $"CAMERA_OFF:{participantId}:{username}";
                        await BroadcastAsync(broadcastMessage, confId, cancellationToken);
                        Console.WriteLine($"Broadcast: CAMERA_OFF for participant {participantId} ({username})");
                    }
                }
            }
        }
    }

    private async Task HandleLeaveConferenceAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string[] parts = message.Substring("LEAVE_CONFERENCE:".Length).Split(':');
        if (parts.Length >= 1 && int.TryParse(parts[0], out int participantId))
        {
            using (var context = new MeetingsContext())
            {
                var participant = context.ConferenceParticipants
                    .FirstOrDefault(cp => cp.Id == participantId);

                if (participant == null) return;

                int confId = participant.ConferenceId;
                var conference = context.Conferences.FirstOrDefault(c => c.ConferenceId == confId);
                string username = participant.UserId != null
                    ? context.Users.FirstOrDefault(u => u.UserId == participant.UserId)?.UserName
                    : context.Guests.FirstOrDefault(g => g.GuestId == participant.GuestId)?.GuestName;

                // Перевіряємо, чи є учасник організатором
                bool isOrganizer = conference != null && conference.OrganizerId != 0 && participant.UserId == conference.OrganizerId;

                if (isOrganizer)
                {
                    // Завершуємо конференцію
                    DatabaseService.EndConference(confId);

                    // Якщо організатор, завершуємо конференцію для всіх
                    var participantsToRemove = context.ConferenceParticipants
                        .Where(cp => cp.ConferenceId == confId)
                        .ToList();

                    // Надсилаємо повідомлення всім учасникам
                    await BroadcastAsync("CONFERENCE_ENDED", confId, cancellationToken);

                    // Даємо клієнтам час обробити повідомлення перед закриттям сокетів
                    await Task.Delay(500, cancellationToken);

                    foreach (var p in participantsToRemove)
                    {
                        if (clientSockets.TryGetValue(p.Id, out var socket))
                        {
                            clients.Remove(socket);
                            clientSockets.Remove(p.Id);
                            try { socket.Shutdown(SocketShutdown.Both); } catch { }
                            socket.Close();
                        }
                    }

                    // Очищаємо список файлів для конференції
                    sharedFiles.RemoveAll(f => context.ConferenceParticipants
                        .Where(cp => cp.Id == f.participantId)
                        .Select(cp => cp.ConferenceId)
                        .FirstOrDefault() == confId);

                    // Використовуємо метод для очищення учасників і пов’язаних повідомлень
                    DatabaseService.ClearParticipantsTable(confId);
                }
                else
                {
                    // Якщо не організатор, видаляємо лише цього учасника
                    var participantIds = new List<int> { participantId };
                    var messagesToDelete = context.ConferenceMessages
                        .Where(cm => participantIds.Contains(cm.SenderId) ||
                                     (cm.ReceiverId.HasValue && participantIds.Contains(cm.ReceiverId.Value)))
                        .ToList();

                    if (messagesToDelete.Any())
                    {
                        context.ConferenceMessages.RemoveRange(messagesToDelete);
                        context.SaveChanges();
                    }

                    context.ConferenceParticipants.Remove(participant);
                    context.SaveChanges();

                    clientSockets.Remove(participantId);
                    clients.Remove(client);
                    await BroadcastAsync($"USER_LEFT:{username}", confId, cancellationToken);
                }
            }
        }
    }

    private async Task HandleScheduleConferenceAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        var parts = message.Substring("SCHEDULE_CONFERENCE:".Length).Split(':');

        string username = parts[0];
        string recievedDateTime = parts[1] + ":" + parts[2] + ":" + parts[3];
        if (!DateTime.TryParse(recievedDateTime, out DateTime startDate))
        {
            await SendToClientAsync(client, "CONFERENCE_SCHEDULE_FAILED", cancellationToken);
            return;
        }

        var (createdConferenceId, participantId) = DatabaseService.CreateConference(username, startDate);

        if (createdConferenceId != null)
        {
            await SendToClientAsync(client, $"CONFERENCE_SCHEDULED:{createdConferenceId}", cancellationToken);
        }
        else
        {
            await SendToClientAsync(client, "CONFERENCE_SCHEDULE_FAILED", cancellationToken);
        }
    }

    private async Task HandleAudioMessageAsync(byte[] buffer, int bytesRead, CancellationToken cancellationToken)
    {
        int headerEnd = -1;
        for (int i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                headerEnd = i;
                break;
            }
        }
        int senderIdEnd = -1;
        for (int i = headerEnd + 1; i < bytesRead; i++)
        {
            if (buffer[i] == (byte)':')
            {
                senderIdEnd = i;
                break;
            }
        }

        if (senderIdEnd > headerEnd && int.TryParse(Encoding.UTF8.GetString(buffer, headerEnd + 1, senderIdEnd - headerEnd - 1), out int senderId))
        {
            using (var context = new MeetingsContext())
            {
                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderId)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();
                await BroadcastAudioAsync(buffer, bytesRead, senderId, confId, cancellationToken);
                UpdateMessage($"Отримано аудіо від учасника {senderId}");
            }
        }
    }

    private async Task BroadcastAudioAsync(byte[] audioData, int length, int senderId, int conferenceId, CancellationToken cancellationToken)
    {
        using (var context = new MeetingsContext())
        {
            var participantIds = context.ConferenceParticipants
                .Where(cp => cp.ConferenceId == conferenceId)
                .Select(cp => cp.Id)
                .ToList();

            foreach (var participantId in participantIds)
            {
                if (participantId != senderId && clientSockets.TryGetValue(participantId, out var client))
                {
                    try
                    {
                        await Task.Factory.FromAsync(
                            client.BeginSend(audioData, 0, length, SocketFlags.None, null, null),
                            client.EndSend).ConfigureAwait(false);
                    }
                    catch
                    {
                        clients.Remove(client);
                        clientSockets.Remove(participantId);
                    }
                }
            }
        }
    }

    private async Task HandleMessagesAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string[] parts = message.Substring("GET_MESSAGES:".Length).Split(':');
        if (parts.Length == 2)
        {
            int id1 = int.Parse(parts[0]);
            string receiverPart = parts[1];

            using (var context = new MeetingsContext())
            {
                List<ConferenceMessage> messages;

                if (receiverPart == "ALL")
                {
                    var confId = context.ConferenceParticipants
                        .Where(cp => cp.Id == id1)
                        .Select(cp => cp.ConferenceId)
                        .FirstOrDefault();

                    messages = context.ConferenceMessages
                        .Where(m => m.ReceiverId == null && m.ConferenceId == confId)
                        .OrderBy(m => m.SentTime)
                        .ToList();
                }
                else
                {
                    int id2 = int.Parse(receiverPart);
                    messages = context.ConferenceMessages
                        .Where(m => (m.SenderId == id1 && m.ReceiverId == id2) ||
                                    (m.SenderId == id2 && m.ReceiverId == id1))
                        .OrderBy(m => m.SentTime)
                        .ToList();
                }

                foreach (var msg in messages)
                {
                    var senderName = context.ConferenceParticipants
                        .Where(cp => cp.Id == msg.SenderId)
                        .Select(cp => cp.User != null ? cp.User.UserName : cp.Guest.GuestName)
                        .FirstOrDefault();

                    string formatted = msg.ReceiverId == null
                        ? $"CHAT_ALL:{msg.SenderId}:{senderName}:{msg.MessageText}"
                        : $"CHAT:{msg.SenderId}:{msg.ReceiverId}:{senderName}:{msg.MessageText}";
                    await SendToClientAsync(client, formatted, cancellationToken);
                }
            }
        }
    }

    private async Task HandleChatAsync(string message, CancellationToken cancellationToken)
    {
        string[] parts = message.Substring("CHAT_ALL:".Length).Split(':', 2);
        if (parts.Length == 2)
        {
            int senderID = int.Parse(parts[0]);
            string chatMessage = parts[1];

            DatabaseService.SaveMessageToDatabase(senderID, null, chatMessage);

            using (var context = new MeetingsContext())
            {
                var senderName = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderID)
                    .Select(cp => cp.User != null ? cp.User.UserName : cp.Guest.GuestName)
                    .FirstOrDefault();

                var confId = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderID)
                    .Select(cp => cp.ConferenceId)
                    .FirstOrDefault();

                string formattedMessage = $"CHAT_ALL:{senderID}:{senderName}:{chatMessage}";
                await BroadcastAsync(formattedMessage, confId, cancellationToken);
                Console.WriteLine($"Broadcast: {formattedMessage}");
            }
        }
    }

    private async Task HandlePrivateChatAsync(string message, CancellationToken cancellationToken)
    {
        string[] parts = message.Substring("CHAT:".Length).Split(':');
        if (parts.Length == 3)
        {
            int senderID = int.Parse(parts[0]);
            string chatMessage = parts[1];
            int receiverID = int.Parse(parts[2]);

            DatabaseService.SaveMessageToDatabase(senderID, receiverID, chatMessage);

            using (var context = new MeetingsContext())
            {
                var senderName = context.ConferenceParticipants
                    .Where(cp => cp.Id == senderID)
                    .Select(cp => cp.User != null ? cp.User.UserName : cp.Guest.GuestName)
                    .FirstOrDefault();

                string formattedMessage = $"CHAT:{senderID}:{receiverID}:{senderName}:{chatMessage}";

                if (clientSockets.ContainsKey(senderID))
                {
                    await SendToClientAsync(clientSockets[senderID], formattedMessage, cancellationToken);
                }

                if (clientSockets.ContainsKey(receiverID))
                {
                    await SendToClientAsync(clientSockets[receiverID], formattedMessage, cancellationToken);
                }
            }
        }
    }

    private async Task HandleRegisterAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        var parts = message.Substring("REGISTER:".Length).Split(':');
        if (DatabaseService.RegisterUser(parts[0], parts[1], parts[2]))
        {
            await SendToClientAsync(client, "REGISTER_SUCCESS", cancellationToken);
            Console.WriteLine("Sent: REGISTER_SUCCESS");
        }
        else
        {
            await SendToClientAsync(client, "REGISTER_FAILED", cancellationToken);
            Console.WriteLine("Sent: REGISTER_FAILED");
        }
    }

    private async Task HandleLoginAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        var parts = message.Substring("LOGIN:".Length).Split(':');
        var result = DatabaseService.LoginUser(parts[0], parts[1]);
        if (result.HasValue)
        {
            userId = result.Value.Item1;
            await SendToClientAsync(client, $"LOGIN_SUCCESS:{result.Value.Item2}", cancellationToken);
            Console.WriteLine($"Sent: LOGIN_SUCCESS:{result.Value.Item2}");
        }
        else
        {
            await SendToClientAsync(client, "LOGIN_FAILED", cancellationToken);
            Console.WriteLine("Sent: LOGIN_FAILED");
        }
    }

    private async Task HandleGuestAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string guestName = message.Substring("GUEST:".Length);
        if (DatabaseService.CreateGuest(guestName))
        {
            await SendToClientAsync(client, "GUEST_SUCCESS", cancellationToken);
            Console.WriteLine($"Sent: GUEST_SUCCESS for {guestName}");
        }
        else
        {
            await SendToClientAsync(client, "GUEST_FAILED", cancellationToken);
            Console.WriteLine($"Sent: GUEST_FAILED for {guestName}");
        }
    }

    private async Task HandleCreateConferenceAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        string username = message.Substring("CREATE_CONFERENCE:".Length);
        var (createdConferenceId, participantId) = DatabaseService.CreateConference(username);

        if (createdConferenceId != null && participantId != null)
        {
            clientSockets[participantId.Value] = client;
            await SendToClientAsync(client, $"CONFERENCE_CREATED:{createdConferenceId}", cancellationToken);

            await Task.Delay(100, cancellationToken);

            await SendToClientAsync(client, $"MY_ID:{participantId}", cancellationToken);

            await Task.Delay(100, cancellationToken);

            Console.WriteLine($"Sent: CONFERENCE_CREATED:{createdConferenceId}");
            await BroadcastAsync($"USER_JOINED:{username}", createdConferenceId.Value, cancellationToken);
        }
        else
        {
            await SendToClientAsync(client, "CONFERENCE_CREATE_FAILED", cancellationToken);
            Console.WriteLine("Sent: CONFERENCE_CREATE_FAILED");
        }
    }

    private async Task HandleJoinConferenceAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        var parts = message.Substring("JOIN_CONFERENCE:".Length).Split(':');
        conferenceId = int.Parse(parts[0]);
        string username = parts[1];

        var result = DatabaseService.JoinConference(conferenceId, username);
        if (result.success)
        {
            int participantId = result.participantId ?? -1;

            await SendToClientAsync(client, $"JOIN_CONFERENCE_SUCCESS", cancellationToken);
            Console.WriteLine($"Sent: JOIN_CONFERENCE_SUCCESS for ParticipantID: {participantId}");

            await Task.Delay(100, cancellationToken);

            clientSockets[participantId] = client;

            await BroadcastAsync($"USER_JOINED:{username}", conferenceId, cancellationToken);
            Console.WriteLine($"Broadcast: USER_JOINED:{username}");

            await Task.Delay(100, cancellationToken);

            await SendToClientAsync(client, $"MY_ID:{participantId}", cancellationToken);
            Console.WriteLine($"Sent: MY_ID:{participantId}");

            await Task.Delay(100, cancellationToken);

            await HandleGetParticipantsAsync(client, message, cancellationToken);
        }
        else
        {
            await SendToClientAsync(client, "JOIN_CONFERENCE_FAILED", cancellationToken);
            Console.WriteLine($"Sent: JOIN_CONFERENCE_FAILED for username: {username}");
        }
    }

    private async Task HandleGetParticipantsAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        var participants = DatabaseService.GetParticipants(conferenceId);
        string response = "PARTICIPANTS:" + string.Join(",", participants);
        await SendToClientAsync(client, response, cancellationToken);
        Console.WriteLine($"Sent: {response}");
    }

    private async Task HandleGetConferenceIDAsync(Socket client, CancellationToken cancellationToken)
    {
        int conferenceID = DatabaseService.GetConferenceID(userId, guestId);
        await SendToClientAsync(client, $"CONFERENCE_ID:{conferenceID}", cancellationToken);
        Console.WriteLine($"Sent: CONFERENCE_ID:{conferenceID}");
    }

    private async Task SendToClientAsync(Socket client, string message, CancellationToken cancellationToken)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message + "\n");
            await Task.Factory.FromAsync(
                client.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                client.EndSend).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendToClientAsync error: {ex.Message}");
            clients.Remove(client);
        }
    }

    private async Task BroadcastAsync(string message, int conferenceId, CancellationToken cancellationToken)
    {
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");
        using (var context = new MeetingsContext())
        {
            var participantIds = context.ConferenceParticipants
                .Where(cp => cp.ConferenceId == conferenceId)
                .Select(cp => cp.Id)
                .ToList();

            foreach (var participantId in participantIds)
            {
                if (clientSockets.TryGetValue(participantId, out var client))
                {
                    try
                    {
                        await Task.Factory.FromAsync(
                            client.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                            client.EndSend).ConfigureAwait(false);
                    }
                    catch
                    {
                        clients.Remove(client);
                        clientSockets.Remove(participantId);
                    }
                }
            }
        }
    }

    private void UpdateMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            textBlock1.Text = message;
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        isRunning = false;
        cts.Cancel();

        foreach (var c in clients.ToList())
        {
            if (c != null && c.Connected)
            {
                try { c.Shutdown(SocketShutdown.Both); } catch { }
                c.Close();
            }
        }
        clients.Clear();

        try { listenerSocket?.Shutdown(SocketShutdown.Both); } catch { }
        listenerSocket?.Close();

        DatabaseService.ClearGuestsTable();
    }
}