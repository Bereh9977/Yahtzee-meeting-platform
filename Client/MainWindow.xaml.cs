using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AForge.Video.DirectShow;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
using System.Drawing.Drawing2D;
using NAudio.Wave;
using System.Text.RegularExpressions;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace Client;

public partial class MainWindow : Window
{
    private Socket clientSocket;
    private string myUsername;
    private string conferenceId;
    private int imageCount = 0;
    private int myParticipantID;
    private NetworkStream networkStream;
    private StreamReader streamReader;
    private bool isClosing = false;
    private Microphone microphoneManager;
    private CancellationTokenSource cts = new CancellationTokenSource();
    private VideoCaptureDevice videoSource;
    private bool cameraStarted = false;
    private Dictionary<string, System.Windows.Controls.Image> userCameraImages = new Dictionary<string, System.Windows.Controls.Image>();
    private Dictionary<string, Border> userBorders = new Dictionary<string, Border>();
    private string lastLoadedChatContext = null;
    private bool shareScreen = false;
    private DispatcherTimer screenCaptureTimer;
    private int? currentScreenParticipantId = null;
    private WasapiLoopbackCapture capture;
    private WaveFileWriter writer;
    private bool recording = false;
    private bool showFiles = false;
    private List<(string senderName, string fileName, byte[] fileData)> receivedFiles = new List<(string, string, byte[])>(); // Updated to store senderName

    public MainWindow()
    {
        InitializeComponent();
    }

    public void ConnectToServer(string username, Socket existingSocket, string confId)
    {
        try
        {
            clientSocket = existingSocket;
            myUsername = username;
            conferenceId = confId;
            Console.WriteLine($"MainWindow: Connected for {username}, Conference ID: {conferenceId}");
            networkStream = new NetworkStream(clientSocket, ownsSocket: false);
            streamReader = new StreamReader(networkStream, Encoding.UTF8, leaveOpen: true);

            InitializeAudio();
            InitializeParticipantPolling();

            Dispatcher.Invoke(() => AddUserImage(username));

            _ = ReceiveMessagesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MainWindow ConnectToServer error: {ex.Message}");
            clientSocket?.Close();
        }
    }

    private void InitializeParticipantPolling()
    {
        var participantTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        participantTimer.Tick += async (s, args) =>
        {
            await SendMessageAsync($"GET_PARTICIPANTS:{conferenceId}");
        };
        participantTimer.Start();
    }

    private void InitializeAudio()
    {
        microphoneManager = new Microphone();
        microphoneManager.InitializeAudio(clientSocket, () => myParticipantID);
        microphoneManager.StartPlayback();
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[25165824];
            while (!isClosing && !cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (!isClosing)
                        Dispatcher.Invoke(() => HandleConferenceEnded());
                    break;
                }

                string msg = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 100));
                if (msg.StartsWith("AUDIO:"))
                {
                    HandleAudioMessage(buffer, bytesRead);
                }
                else if (msg.StartsWith("VIDEO:"))
                {
                    HandleVideoMessage(buffer, bytesRead);
                }
                else if (msg.StartsWith("SCREEN:"))
                {
                    HandleScreenMessage(buffer, bytesRead);
                }
                else if (msg.StartsWith("FILE:"))
                {
                    HandleFileMessage(buffer, bytesRead);
                }
                else if (msg.StartsWith("FILE:"))
                {
                    HandleFileMessage(buffer, bytesRead);
                }
                else
                {
                    string[] lines = msg.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("USER_JOINED:")) HandleUserJoined(line);
                        else if (line.StartsWith("MY_ID:")) HandleMyId(line);
                        else if (line.StartsWith("CHAT_ALL:")) HandleChatAll(line);
                        else if (line.StartsWith("CHAT:")) HandlePrivateChat(line);
                        else if (line.StartsWith("PARTICIPANTS:")) HandleParticipants(line);
                        else if (line.StartsWith("USER_LEFT:")) HandleUserLeft(line);
                        else if (line.StartsWith("CONFERENCE_ENDED") && !isClosing) HandleConferenceEnded();
                        else if (line.StartsWith("CAMERA_OFF:"))
                        {
                            HandleCameraOff(line);
                        }
                        else if (line.StartsWith("SCREEN_OFF:"))
                        {
                            HandleScreenOff(line);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!isClosing && !cancellationToken.IsCancellationRequested)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Помилка при отриманні повідомлень: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                    HandleConferenceEnded();
                });
            }
        }
    }


    private void HandleFileMessage(byte[] buffer, int bytesRead)
    {
        string msg = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 100));
        int headerEnd = msg.IndexOf(':') + 1;
        int senderIdEnd = msg.IndexOf(':', headerEnd);
        int senderNameEnd = msg.IndexOf(':', senderIdEnd + 1);
        int fileNameEnd = msg.IndexOf(':', senderNameEnd + 1);
        if (fileNameEnd > senderNameEnd && int.TryParse(msg.Substring(headerEnd, senderIdEnd - headerEnd), out int senderId))
        {
            string senderName = msg.Substring(senderIdEnd + 1, senderNameEnd - senderIdEnd - 1);
            string fileName = msg.Substring(senderNameEnd + 1, fileNameEnd - senderNameEnd - 1);
            int fileStart = fileNameEnd + 1;
            byte[] fileData = new byte[bytesRead - fileStart];
            Buffer.BlockCopy(buffer, fileStart, fileData, 0, fileData.Length);

            Console.WriteLine($"Received file {fileName} from {senderName} (ID: {senderId}), size: {fileData.Length} bytes");

            Dispatcher.Invoke(() =>
            {
                receivedFiles.Add((senderName, fileName, fileData));
                if (showFiles)
                {
                    UpdateFileList();
                }
            });
        }
        else
        {
            Console.WriteLine($"Invalid file message format, size: {bytesRead} bytes");
        }
    }

    private void UpdateFileList()
    {
        chatBox.Items.Clear();
        foreach (var (senderName, fileName, _) in receivedFiles)
        {
            chatBox.Items.Add($"{senderName}: {fileName}");
        }
    }


    private void HandleScreenMessage(byte[] buffer, int bytesRead)
    {
        string msg = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 100));
        int headerEnd = msg.IndexOf(':') + 1;
        int senderIdEnd = msg.IndexOf(':', headerEnd);
        if (senderIdEnd > headerEnd && int.TryParse(msg.Substring(headerEnd, senderIdEnd - headerEnd), out int senderId))
        {
            int screenStart = senderIdEnd + 1;
            byte[] screenData = new byte[bytesRead - screenStart];
            Buffer.BlockCopy(buffer, screenStart, screenData, 0, screenData.Length);

            Console.WriteLine($"Received screen frame from {senderId}, size: {screenData.Length} bytes");

            Dispatcher.Invoke(() =>
            {
                if (senderId != myParticipantID)
                {
                    currentScreenParticipantId = senderId;
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(screenData))
                        {
                            BitmapImage bImg = new BitmapImage();
                            bImg.BeginInit();
                            bImg.StreamSource = ms;
                            bImg.CacheOption = BitmapCacheOption.OnLoad;
                            bImg.EndInit();
                            bImg.Freeze();
                            ShareImage.Source = bImg;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error displaying screen for participant {senderId}: {ex.Message}");
                    }
                }
            });
        }
        else
        {
            Console.WriteLine($"Invalid screen message format, size: {bytesRead} bytes");
        }
    }

    private void HandleScreenOff(string msg)
    {
        string participantIdStr = msg.Substring("SCREEN_OFF:".Length);
        if (int.TryParse(participantIdStr, out int participantId))
        {
            Console.WriteLine($"Received SCREEN_OFF from participant {participantId}");
            Dispatcher.Invoke(() =>
            {
                if (currentScreenParticipantId == participantId)
                {
                    ShareImage.Source = null;
                    currentScreenParticipantId = null;
                    Console.WriteLine($"Cleared ShareImage for participant {participantId}");
                }
            });
        }
        else
        {
            Console.WriteLine($"Invalid SCREEN_OFF message format: {msg}");
        }
    }

    private void HandleCameraOff(string msg)
    {
        string[] parts = msg.Substring("CAMERA_OFF:".Length).Split(':');
        if (parts.Length == 2)
        {
            int participantId = int.Parse(parts[0]);
            string username = parts[1];

            Console.WriteLine($"Processing CAMERA_OFF for {username} (ID: {participantId})");
            Dispatcher.Invoke(() =>
            {
                if (username != myUsername)
                {
                    ReplaceWithBorder(username);
                    Console.WriteLine($"Replaced camera with border for {username}");
                }
            });
        }
    }

    private void HandleVideoMessage(byte[] buffer, int bytesRead)
    {
        string msg = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 100));
        int headerEnd = msg.IndexOf(':') + 1;
        int senderIdEnd = msg.IndexOf(':', headerEnd);
        if (senderIdEnd > headerEnd && int.TryParse(msg.Substring(headerEnd, senderIdEnd - headerEnd), out int senderId))
        {
            int videoStart = senderIdEnd + 1;
            byte[] videoData = new byte[bytesRead - videoStart];
            Buffer.BlockCopy(buffer, videoStart, videoData, 0, videoData.Length);

            Console.WriteLine($"Received video frame from {senderId}, size: {videoData.Length} bytes");

            Dispatcher.Invoke(() =>
            {
                string username = GetUsernameByParticipantId(senderId);
                if (!string.IsNullOrEmpty(username) && username != myUsername)
                {
                    try
                    {
                        if (!userCameraImages.ContainsKey(username))
                        {
                            if (!userBorders.ContainsKey(username))
                            {
                                AddUserImage(username);
                            }
                            ReplaceWithCamera(username);
                        }

                        if (userCameraImages.TryGetValue(username, out var imageControl))
                        {
                            using (MemoryStream ms = new MemoryStream(videoData))
                            {
                                BitmapImage bImg = new BitmapImage();
                                bImg.BeginInit();
                                bImg.StreamSource = ms;
                                bImg.CacheOption = BitmapCacheOption.OnLoad;
                                bImg.EndInit();
                                bImg.Freeze();
                                imageControl.Source = bImg;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error displaying video for {username}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"No username found for participant {senderId}");
                }
            });
        }
        else
        {
            Console.WriteLine($"Invalid video message format, size: {bytesRead} bytes");
        }
    }

    private string GetUsernameByParticipantId(int participantId)
    {
        var participantPairs = comboboxParticipants.Tag as System.Collections.Generic.List<string[]>;
        if (participantPairs != null)
        {
            Console.WriteLine($"Participant pairs: {string.Join(", ", participantPairs.Select(p => $"[{p[0]}:{p[1]}]"))}");
            foreach (var pair in participantPairs)
            {
                if (pair[0] == participantId.ToString())
                {
                    Console.WriteLine($"Found username {pair[1]} for participantId {participantId}");
                    return pair[1];
                }
            }
            Console.WriteLine($"No username found for participantId {participantId}");
        }
        return null;
    }

    private void HandleAudioMessage(byte[] buffer, int bytesRead)
    {
        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        int headerEnd = msg.IndexOf(':') + 1;
        int senderIdEnd = msg.IndexOf(':', headerEnd);
        if (senderIdEnd > headerEnd && int.TryParse(msg.Substring(headerEnd, senderIdEnd - headerEnd), out int senderId))
        {
            if (senderId != myParticipantID)
            {
                int audioStart = senderIdEnd + 1;
                byte[] audioData = new byte[bytesRead - audioStart];
                Buffer.BlockCopy(buffer, audioStart, audioData, 0, audioData.Length);
                microphoneManager.AddAudioSamples(audioData);
            }
        }
    }

    private void HandleUserJoined(string msg)
    {
        string username = msg.Substring("USER_JOINED:".Length);
        if (username != myUsername)
        {
            Console.WriteLine($"Processing USER_JOINED for {username}");
            Dispatcher.Invoke(() => AddUserImage(username));
        }
        else
        {
            Console.WriteLine($"Ignoring USER_JOINED for own username: {username}");
        }
    }

    private void HandleUserLeft(string msg)
    {
        string username = msg.Substring("USER_LEFT:".Length);
        Console.WriteLine($"Processing USER_LEFT for {username}");
        Dispatcher.Invoke(() =>
        {
            if (userBorders.ContainsKey(username))
            {
                var border = userBorders[username];
                stackPanelUsers.Children.Remove(border);
                userBorders.Remove(username);
                userCameraImages.Remove(username);
                imageCount--;
                Console.WriteLine($"Removed user image for {username}, total images: {imageCount}");
            }

            string leavingUserId = GetUsernameByParticipantId(myParticipantID);
            if (currentScreenParticipantId.HasValue && GetUsernameByParticipantId(currentScreenParticipantId.Value) == username)
            {
                ShareImage.Source = null;
                currentScreenParticipantId = null;
            }

            _ = SendMessageAsync($"GET_PARTICIPANTS:{conferenceId}");
        });
    }

    private void HandleConferenceEnded()
    {
        if (isClosing) return;

        Dispatcher.Invoke(() =>
        {
            MessageBox.Show("Конференція завершена організатором.", "Інформація", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = DisconnectFromConferenceAsync();
        });
    }

    private void HandleMyId(string msg)
    {
        myParticipantID = int.Parse(msg.Substring("MY_ID:".Length));
    }

    private void HandleChatAll(string msg)
    {
        string[] parts = msg.Substring("CHAT_ALL:".Length).Split(':', 3);
        if (parts.Length == 3)
        {
            int senderID = int.Parse(parts[0]);
            string senderName = parts[1];
            string chatMessage = parts[2];

            Dispatcher.Invoke(() =>
            {
                if (comboboxParticipants.SelectedIndex == 0)
                {
                    chatBox.Items.Add($"{senderName}: {chatMessage}");
                }
            });
        }
    }

    private void HandlePrivateChat(string msg)
    {
        string[] parts = msg.Substring("CHAT:".Length).Split(':', 4);
        if (parts.Length == 4)
        {
            int senderID = int.Parse(parts[0]);
            int receiverID = int.Parse(parts[1]);
            string senderName = parts[2];
            string chatMessage = parts[3];

            Dispatcher.Invoke(() =>
            {
                var selectedIndex = comboboxParticipants.SelectedIndex;
                var participantPairs = comboboxParticipants.Tag as List<string[]>;

                if (selectedIndex > 0 && participantPairs != null)
                {
                    var selectedParticipant = participantPairs[selectedIndex - 1];
                    int selectedParticipantID = int.Parse(selectedParticipant[0]);

                    if ((senderID == myParticipantID && receiverID == selectedParticipantID) ||
                        (senderID == selectedParticipantID && receiverID == myParticipantID))
                    {
                        chatBox.Items.Add($"{senderName}: {chatMessage}");
                    }
                }
            });
        }
    }

    private void HandleParticipants(string msg)
    {
        string participantsList = msg.Substring("PARTICIPANTS:".Length);
        string[] participants = participantsList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        int myID = myParticipantID;

        var participantPairs = participants
            .Select(p => p.Split(':'))
            .Where(split => split.Length == 2)
            .Where(split => int.TryParse(split[0], out int id) && id != myID)
            .ToList();

        Dispatcher.Invoke(() =>
        {
            var participantList = participantPairs.Select(p => p[1]).ToList();
            participantList.Insert(0, "All");
            comboboxParticipants.ItemsSource = participantList;
            comboboxParticipants.Tag = participantPairs;
            if (comboboxParticipants.SelectedIndex == -1)
            {
                comboboxParticipants.SelectedIndex = 0;
            }

            var currentParticipants = participantPairs.Select(p => p[1]).ToHashSet();
            var existingParticipants = userBorders.Keys.ToList();

            foreach (var participant in participantPairs)
            {
                string username = participant[1];
                if (username != myUsername && !userBorders.ContainsKey(username) && !userCameraImages.ContainsKey(username))
                {
                    AddUserImage(username);
                }
            }

            foreach (var username in existingParticipants)
            {
                if (!currentParticipants.Contains(username) && username != myUsername)
                {
                    if (userBorders.ContainsKey(username))
                    {
                        var border = userBorders[username];
                        stackPanelUsers.Children.Remove(border);
                        userBorders.Remove(username);
                        userCameraImages.Remove(username);
                        imageCount--;
                        Console.WriteLine($"Removed user image for {username}, total images: {imageCount}");
                    }
                }
            }

            if (comboboxParticipants.SelectedIndex == 0 && lastLoadedChatContext != "ALL")
            {
                _ = SendMessageAsync($"GET_MESSAGES:{myParticipantID}:ALL");
                lastLoadedChatContext = "ALL";
            }
        });
    }

    private void AddUserImage(string username)
    {
        if (userBorders.ContainsKey(username) || userCameraImages.ContainsKey(username))
        {
            Console.WriteLine($"User {username} already added, skipping.");
            return;
        }

        Console.WriteLine($"Adding user image for {username}");
        var border = new Border
        {
            Width = 192,
            Height = 120,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3E, 0x3E, 0x3E)),
            BorderBrush = System.Windows.Media.Brushes.Black,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 10, 0, 0)
        };

        var label = new TextBlock
        {
            Text = username,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            FontSize = 16,
            FontWeight = FontWeights.Bold
        };

        var grid = new Grid();
        grid.Children.Add(label);
        border.Child = grid;

        stackPanelUsers.Children.Add(border);
        userBorders[username] = border;
        imageCount++;
        Console.WriteLine($"Added user image for {username}, total images: {imageCount}");
    }

    private async Task SendMessageAsync(string msg)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            await Task.Factory.FromAsync(
                clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                clientSocket.EndSend).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendMessageAsync error: {ex.Message}");
        }
    }

    private async Task SendVideoFrameAsync(byte[] videoData)
    {
        byte[] header = Encoding.UTF8.GetBytes($"VIDEO:{myParticipantID}:");
        byte[] data = new byte[header.Length + videoData.Length];
        Buffer.BlockCopy(header, 0, data, 0, header.Length);
        Buffer.BlockCopy(videoData, 0, data, header.Length, videoData.Length);

        Console.WriteLine($"Sending video frame, size: {data.Length} bytes");
        if (data.Length > 8388608)
        {
            Console.WriteLine("Warning: Video frame size exceeds buffer limit of 8MB!");
            return;
        }

        await Task.Factory.FromAsync(
            clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
            clientSocket.EndSend).ConfigureAwait(false);
    }

    private async Task SendScreenFrameAsync(byte[] screenData)
    {
        try
        {
            byte[] header = Encoding.UTF8.GetBytes($"SCREEN:{myParticipantID}:");
            byte[] data = new byte[header.Length + screenData.Length];
            Buffer.BlockCopy(header, 0, data, 0, header.Length);
            Buffer.BlockCopy(screenData, 0, data, header.Length, screenData.Length);

            Console.WriteLine($"Sending screen frame, size: {data.Length} bytes");
            if (data.Length > 8388608)
            {
                Console.WriteLine("Warning: Screen frame size exceeds buffer limit of 8MB!");
                return;
            }

            await Task.Factory.FromAsync(
                clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                clientSocket.EndSend).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendScreenFrameAsync error: {ex.Message}");
        }
    }

    private void ReplaceWithCamera(string username)
    {
        Dispatcher.Invoke(() =>
        {
            if (userBorders.ContainsKey(username))
            {
                var existingBorder = userBorders[username];
                stackPanelUsers.Children.Remove(existingBorder);
                userBorders.Remove(username);
                userCameraImages.Remove(username);
            }

            var border = new Border
            {
                Width = 192,
                Height = 120,
                Background = System.Windows.Media.Brushes.Black,
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10, 10, 0, 0)
            };

            var image = new System.Windows.Controls.Image
            {
                Stretch = Stretch.UniformToFill
            };

            var grid = new Grid();
            grid.Children.Add(image);
            border.Child = grid;

            stackPanelUsers.Children.Add(border);
            userCameraImages[username] = image;
            userBorders[username] = border;
        });
    }

    private void ReplaceWithBorder(string username)
    {
        Dispatcher.Invoke(() =>
        {
            if (userBorders.ContainsKey(username))
            {
                var existingBorder = userBorders[username];
                stackPanelUsers.Children.Remove(existingBorder);
                userBorders.Remove(username);
                userCameraImages.Remove(username);
            }

            var border = new Border
            {
                Width = 192,
                Height = 120,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x3E, 0x3E, 0x3E)),
                BorderBrush = System.Windows.Media.Brushes.Black,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10, 10, 0, 0)
            };

            var label = new TextBlock
            {
                Text = username,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };

            var grid = new Grid();
            grid.Children.Add(label);
            border.Child = grid;

            stackPanelUsers.Children.Add(border);
            userBorders[username] = border;
            userCameraImages.Remove(username);
        });
    }

    private void StartCamera()
    {
        var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

        videoSource = new VideoCaptureDevice(videoDevices[0].MonikerString);
        var capabilities = videoSource.VideoCapabilities;
        if (capabilities.Any())
        {
            videoSource.VideoResolution = capabilities.FirstOrDefault(c => c.FrameSize.Width <= 640) ?? capabilities[0];
        }

        long lastFrameTime = 0;
        long frameInterval = 10000000 / 10; // 10 FPS

        videoSource.NewFrame += (sender, eventArgs) =>
        {
            long currentTime = DateTime.Now.Ticks;
            if (currentTime - lastFrameTime < frameInterval)
                return;
            lastFrameTime = currentTime;

            Bitmap bitmap = (Bitmap)eventArgs.Frame.Clone();
            using (MemoryStream ms = new MemoryStream())
            {
                var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L); // Якість 60%
                bitmap.Save(ms, encoder, encParams);
                byte[] videoData = ms.ToArray();

                File.WriteAllBytes($"frame_{DateTime.Now.Ticks}.jpg", videoData);
                Console.WriteLine($"Saved frame, size: {videoData.Length} bytes");

                if (videoData.Length > 8000000)
                {
                    Console.WriteLine($"Warning: Frame size {videoData.Length} bytes exceeds safe limit!");
                    bitmap.Dispose();
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (userCameraImages.TryGetValue(myUsername, out var imageControl))
                    {
                        BitmapImage bImg = new BitmapImage();
                        bImg.BeginInit();
                        bImg.StreamSource = new MemoryStream(videoData);
                        bImg.CacheOption = BitmapCacheOption.OnLoad;
                        bImg.EndInit();
                        bImg.Freeze();
                        imageControl.Source = bImg;
                    }
                });

                _ = SendVideoFrameAsync(videoData);
            }
            bitmap.Dispose();
        };

        videoSource.Start();
        ReplaceWithCamera(myUsername);
    }

    private void StopCamera()
    {
        Console.WriteLine("Stopping camera...");
        if (videoSource != null && videoSource.IsRunning)
        {
            videoSource.SignalToStop();
            videoSource.WaitForStop();
            Console.WriteLine("Camera stopped successfully");
        }
        videoSource = null;

        ReplaceWithBorder(myUsername);

        _ = SendMessageAsync($"CAMERA_OFF:{myParticipantID}");
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e)
    {
        string text = chatInput.Text.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var selectedIndex = comboboxParticipants.SelectedIndex;
            var participantPairs = comboboxParticipants.Tag as List<string[]>;

            if (selectedIndex != -1 && participantPairs != null)
            {
                if (selectedIndex == 0)
                {
                    string message = $"CHAT_ALL:{myParticipantID}: {text}";
                    await SendMessageAsync(message);
                    chatInput.Text = "";
                }
                else
                {
                    var selectedParticipant = participantPairs[selectedIndex - 1];
                    if (selectedParticipant != null)
                    {
                        string receiverID = selectedParticipant[0];
                        string message = $"CHAT:{myParticipantID}: {text}:{receiverID}";
                        await SendMessageAsync(message);
                        chatInput.Text = "";
                    }
                }
            }
        }
    }

    private void SaveChat_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Forms.SaveFileDialog saveFileDialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Зберегти історію чату",
            Filter = "Текстовий файл (*.txt)|*.txt",
            DefaultExt = "txt",
            FileName = "ChatHistory.txt"
        };

        if (saveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var item in chatBox.Items)
            {
                sb.AppendLine(item.ToString());
            }

            File.WriteAllText(saveFileDialog.FileName, sb.ToString(), Encoding.UTF8);

            MessageBox.Show("Файл успішно збережено!", "Успіх", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ShowChatHistory(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = comboboxParticipants.SelectedItem as string;
        var participantPairs = comboboxParticipants.Tag as List<string[]>;
        var selectedIndex = comboboxParticipants.SelectedIndex;

        if (selectedItem != null && participantPairs != null)
        {
            if (selectedIndex == 0)
            {
                if (lastLoadedChatContext != "ALL")
                {
                    Dispatcher.Invoke(() => chatBox.Items.Clear());
                    await SendMessageAsync($"GET_MESSAGES:{myParticipantID}:ALL");
                    lastLoadedChatContext = "ALL";
                }
            }
            else
            {
                var selectedParticipant = participantPairs.FirstOrDefault(p => p[1] == selectedItem);
                if (selectedParticipant != null)
                {
                    string receiverID = selectedParticipant[0];
                    if (lastLoadedChatContext != receiverID)
                    {
                        Dispatcher.Invoke(() => chatBox.Items.Clear());
                        await SendMessageAsync($"GET_MESSAGES:{myParticipantID}:{receiverID}");
                        lastLoadedChatContext = receiverID;
                    }
                }
            }
        }
    }

    private async void ShowChat_Click(object sender, RoutedEventArgs e)
    {
        if (ChatElemets.Visibility == Visibility.Collapsed)
        {
            ChatElemets.Visibility = Visibility.Visible;
            ButtonChat.Tag = "Active";
        }
        else
        {
            ChatElemets.Visibility = Visibility.Collapsed;
            ButtonChat.Tag = null;
        }
        Dispatcher.Invoke(() => chatBox.Items.Clear());
        lastLoadedChatContext = null;
        await SendMessageAsync($"GET_PARTICIPANTS:{conferenceId}");
    }

    private void GetConferenceID_Click(object sender, RoutedEventArgs e)
    {
        string conferenceID = conferenceId.ToString();
        Dispatcher.Invoke(() =>
        {
            Clipboard.SetText(conferenceID);
            CopiedMessage.Visibility = Visibility.Visible;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            timer.Tick += (s, args) =>
            {
                CopiedMessage.Visibility = Visibility.Collapsed;
                timer.Stop();
            };

            timer.Start();
        });
    }

    private async Task DisconnectFromConferenceAsync()
    {
        if (isClosing) return;

        isClosing = true;
        try
        {
            cts.Cancel();

            microphoneManager?.Dispose();
            StopCamera();
            streamReader?.Dispose();
            networkStream?.Dispose();

            await SendMessageAsync($"LEAVE_CONFERENCE:{myParticipantID}");
            await Task.Delay(100);

            if (clientSocket != null && clientSocket.Connected)
            {
                try { clientSocket.Shutdown(SocketShutdown.Both); } catch { }
                clientSocket.Close();
            }
            clientSocket = null;

            Socket newSocket = null;
            try
            {
                newSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await Task.Factory.FromAsync(
                    newSocket.BeginConnect,
                    newSocket.EndConnect,
                    "0.tcp.eu.ngrok.io",
                    19916,
                    null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create new socket: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Не вдалося підключитися до сервера: {ex.Message}. Програма буде закрита.", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (newSocket != null && newSocket.Connected)
                {
                    ConferenceSelectionWindow selectionWindow = new ConferenceSelectionWindow(myUsername, newSocket);
                    selectionWindow.Show();
                    Close();
                }
                else
                {
                    MessageBox.Show("Не вдалося встановити нове з'єднання з сервером.", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DisconnectFromConferenceAsync error: {ex.Message}");
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"Помилка при виході з конференції: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            });
        }
    }

    private async void ButtonLeave_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectFromConferenceAsync();
    }

    private void ButtonMicrophone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool isOn = microphoneManager.ToggleMicrophone();

            if (isOn)
            {
                ButtonMicrophone.Tag = "Active";
            }
            else
            {
                ButtonMicrophone.Tag = null;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error toggling microphone: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ButtonCamera_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraStarted)
        {
            ButtonCamera.Tag = "Active";
            StartCamera();
            cameraStarted = true;
        }
        else
        {
            ButtonCamera.Tag = null;
            StopCamera();
            cameraStarted = false;
        }
    }

    private async void ButtomShareScreen_Click(object sender, RoutedEventArgs e)
    {
        if (!shareScreen)
        {
            if (currentScreenParticipantId != null)
            {
                MessageBox.Show("Демонстрацію уже хтось показує!", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            shareScreen = true;
            ButtonShareScreen.Tag = "Active";

            if (screenCaptureTimer == null)
            {
                screenCaptureTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100) // 10 FPS
                };

                screenCaptureTimer.Tick += async (s, args) =>
                {
                    try
                    {
                        using (Bitmap bmp = new Bitmap(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width,
                                                       System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height,
                                                       System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                            {
                                g.CopyFromScreen(System.Windows.Forms.Screen.PrimaryScreen.Bounds.X,
                                                 System.Windows.Forms.Screen.PrimaryScreen.Bounds.Y,
                                                 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                            }

                            int targetWidth = 1280;
                            int targetHeight = 720;
                            using (Bitmap resizedBmp = new Bitmap(targetWidth, targetHeight))
                            {
                                using (Graphics g = Graphics.FromImage(resizedBmp))
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
                                }

                                using (MemoryStream ms = new MemoryStream())
                                {
                                    var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                    var encParams = new EncoderParameters(1);
                                    encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);
                                    resizedBmp.Save(ms, encoder, encParams);

                                    byte[] screenData = ms.ToArray();
                                    Console.WriteLine($"Captured screen frame, size: {screenData.Length} bytes");

                                    if (screenData.Length > 8000000)
                                    {
                                        Console.WriteLine("Warning: Screen frame size exceeds safe limit!");
                                        return;
                                    }

                                    await SendScreenFrameAsync(screenData);

                                    Dispatcher.Invoke(() =>
                                    {
                                        BitmapImage bitmapImage = new BitmapImage();
                                        bitmapImage.BeginInit();
                                        bitmapImage.StreamSource = new MemoryStream(screenData);
                                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                        bitmapImage.EndInit();
                                        bitmapImage.Freeze();
                                        ShareImage.Source = bitmapImage;
                                        currentScreenParticipantId = myParticipantID;
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Screen capture error: {ex.Message}");
                    }
                };
            }

            screenCaptureTimer.Start();
        }
        else
        {
            shareScreen = false;
            ButtonShareScreen.Tag = null;
            ShareImage.Source = null;
            currentScreenParticipantId = null;

            screenCaptureTimer?.Stop();
            await Task.Delay(100);
            await SendMessageAsync($"SCREEN_OFF:{myParticipantID}");
        }
    }

    private void ButtonRecord_Click(object sender, RoutedEventArgs e)
    {
        string fileName = $"record_{DateTime.Now:yyyyMMdd_HHmmss}.wav";

        if (recording == false)
        {
            capture = new WasapiLoopbackCapture();
            writer = new WaveFileWriter(fileName, capture.WaveFormat);
            capture.DataAvailable += (s, a) =>
            {
                writer.Write(a.Buffer, 0, a.BytesRecorded);
            };

            capture.RecordingStopped += (s, a) =>
            {
                writer?.Dispose();
                capture?.Dispose();
            };

            capture.StartRecording();
            ButtonRecord.Tag = "Active";
            recording = true;
        }

        else
        {
            capture?.StopRecording();
            ButtonRecord.Tag = null;
            recording = false;
        }
    }

    private async Task SendFileAsync(string filePath)
    {
        try
        {
            string fileName = Path.GetFileName(filePath);
            byte[] fileData = File.ReadAllBytes(filePath);
            if (fileData.Length > 8388608)
            {
                MessageBox.Show("Файл перевищує ліміт у 8 МБ!", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            byte[] header = Encoding.UTF8.GetBytes($"FILE:{myParticipantID}:{myUsername}:{fileName}:");
            byte[] data = new byte[header.Length + fileData.Length];
            Buffer.BlockCopy(header, 0, data, 0, header.Length);
            Buffer.BlockCopy(fileData, 0, data, header.Length, fileData.Length);

            Console.WriteLine($"Sending file {fileName} from {myUsername}, size: {data.Length} bytes");

            await Task.Factory.FromAsync(
                clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, null, null),
                clientSocket.EndSend).ConfigureAwait(false);

            Dispatcher.Invoke(() =>
            {
                receivedFiles.Add((myUsername, fileName, fileData));
                if (showFiles)
                {
                    UpdateFileList();
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendFileAsync error: {ex.Message}");
            MessageBox.Show($"Помилка при відправці файлу: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UploadFile_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Forms.OpenFileDialog openFileDialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Виберіть файл для відправки",
            Filter = "Усі файли (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            await SendFileAsync(openFileDialog.FileName);
        }
    }

    private async void ChatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (showFiles && chatBox.SelectedItem != null)
        {
            string selectedItem = chatBox.SelectedItem.ToString();
            // Розділяємо рядок на senderName і fileName
            var parts = selectedItem.Split(':');
            if (parts.Length < 2) return; // Перевірка на правильний формат
            string senderName = parts[0].Trim();
            string fileName = parts[1].Trim();

            var file = receivedFiles.FirstOrDefault(f => f.senderName == senderName && f.fileName == fileName);
            if (file.fileData != null)
            {
                System.Windows.Forms.SaveFileDialog saveFileDialog = new System.Windows.Forms.SaveFileDialog
                {
                    Title = "Зберегти файл",
                    Filter = "Усі файли (.)|.",
                    FileName = file.fileName
                };

                if (saveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllBytes(saveFileDialog.FileName, file.fileData);
                        MessageBox.Show("Файл успішно збережено!", "Успіх", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Помилка при збереженні файлу: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                Console.WriteLine($"File not found for sender: {senderName}, file: {fileName}");
            }
        }
    }

    private async void ButtonShowFiles_Click(object sender, RoutedEventArgs e)
    {
        showFiles = !showFiles;
        Dispatcher.Invoke(() =>
        {
            chatBox.Items.Clear();
            if (showFiles)
            {
                UpdateFileList();
            }
            else
            {
                lastLoadedChatContext = null;
                chatBox.Items.Clear();
            }
        });
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!isClosing)
        {
            e.Cancel = true;
            await DisconnectFromConferenceAsync();
        }
    }
}