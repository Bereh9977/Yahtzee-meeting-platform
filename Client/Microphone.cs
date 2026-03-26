using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using System;
using System.Net.Sockets;

namespace Client
{
    public class Microphone : IDisposable
    {
        private WaveInEvent waveIn;
        private WaveOutEvent waveOut;
        private BufferedWaveProvider waveProvider;
        private bool isMicrophoneOn = false;
        private Socket clientSocket;
        private Func<int> getParticipantId;

        public bool IsMicrophoneOn => isMicrophoneOn;

        public void InitializeAudio(Socket socket, Func<int> participantIdGetter)
        {
            clientSocket = socket;
            getParticipantId = participantIdGetter;

            // Налаштування захоплення аудіо
            waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1), // 44.1kHz, 16-bit, моно
                BufferMilliseconds = 100 // Низька затримка
            };
            waveIn.DataAvailable += WaveIn_DataAvailable;

            // Налаштування відтворення аудіо
            waveOut = new WaveOutEvent();
            waveProvider = new BufferedWaveProvider(waveIn.WaveFormat);
            waveOut.Init(waveProvider);
        }

        public void StartPlayback()
        {
            waveOut?.Play();
        }

        public void AddAudioSamples(byte[] audioData)
        {
            waveProvider?.AddSamples(audioData, 0, audioData.Length);
        }

        public bool ToggleMicrophone()
        {
            isMicrophoneOn = !isMicrophoneOn;
            if (isMicrophoneOn)
            {
                waveIn?.StartRecording();
                Console.WriteLine("Microphone ON");
            }
            else
            {
                waveIn?.StopRecording();
                Console.WriteLine("Microphone OFF");
            }
            return isMicrophoneOn;
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (isMicrophoneOn && clientSocket != null && clientSocket.Connected)
            {
                try
                {
                    byte[] header = Encoding.UTF8.GetBytes($"AUDIO:{getParticipantId()}:");
                    byte[] data = new byte[header.Length + e.BytesRecorded];
                    Buffer.BlockCopy(header, 0, data, 0, header.Length);
                    Buffer.BlockCopy(e.Buffer, 0, data, header.Length, e.BytesRecorded);
                    clientSocket.Send(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending audio: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            waveIn?.StopRecording();
            waveOut?.Stop();
            waveIn?.Dispose();
            waveOut?.Dispose();
            waveProvider = null;
            clientSocket = null;
        }
    }
}