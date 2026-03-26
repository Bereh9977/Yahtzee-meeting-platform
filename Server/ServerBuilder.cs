using System.Net;
using System.Net.Sockets;

namespace Server;

public class ServerBuilder
{
    private int port;
    private Socket listenerSocket;

    public ServerBuilder(int port)
    {
        this.port = port;
    }

    public Socket Build()
    {
        listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listenerSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        listenerSocket.Listen(10);
        return listenerSocket;
    }
}