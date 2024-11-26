using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class MdnsService
{
    private const string MulticastAddress = "224.0.0.251";
    private const int MulticastPort = 5353;
    private const string ServiceType = "_noodles._tcp.local.";
    private readonly UdpClient _udp_client;
    private readonly string _service_name;
    private readonly int _port;
    private CancellationTokenSource _cancellation = new();

    public MdnsService(string service_name, int port)
    {
        _service_name = service_name;
        _port = port;
        _udp_client = new UdpClient
        {
            MulticastLoopback = false
        };
        _udp_client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
    }

    public void Start()
    {
        Task.Run(() => BroadcastService(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation.Cancel();
        _udp_client.DropMulticastGroup(IPAddress.Parse(MulticastAddress));
        _udp_client.Close();
    }

    private async Task BroadcastService(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            byte[] packet = CreateMdnsPacket();
            _udp_client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort));
            await Task.Delay(5000); // Broadcast every 5 seconds
        }
    }

    private byte[] CreateMdnsPacket()
    {
        // Basic mDNS packet structure
        string service_announcement = $"{_service_name}.{ServiceType}";
        //string txtRecord = "path=/";
        byte[] header = new byte[]
        {
            0x00, 0x00, // Transaction ID
            0x84, 0x00, // Flags (response, authoritative answer)
            0x00, 0x01, // Questions count
            0x00, 0x01, // Answer RRs
            0x00, 0x00, // Authority RRs
            0x00, 0x00  // Additional RRs
        };
        byte[] question = EncodeMdnsName(service_announcement);
        byte[] question_type_class = new byte[] { 0x00, 0x01, 0x80, 0x01 }; // Type A, class IN, flush cache
        byte[] answer = EncodeMdnsName(service_announcement);
        byte[] answertype_class_TTL = new byte[] { 0x00, 0x01, 0x80, 0x01, 0x00, 0x00, 0x00, 0x78 }; // Type A, class IN, TTL
        byte[] address = IPAddress.Parse(GetLocalIPAddress()).GetAddressBytes(); 
        byte[] packet = new byte[header.Length + question.Length + question_type_class.Length + answer.Length + answertype_class_TTL.Length + address.Length];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(question, 0, packet, header.Length, question.Length);
        Buffer.BlockCopy(question_type_class, 0, packet, header.Length + question.Length, question_type_class.Length);
        Buffer.BlockCopy(answer, 0, packet, header.Length + question.Length + question_type_class.Length, answer.Length);
        Buffer.BlockCopy(answertype_class_TTL, 0, packet, header.Length + question.Length + question_type_class.Length + answer.Length, answertype_class_TTL.Length);
        Buffer.BlockCopy(address, 0, packet, header.Length + question.Length + question_type_class.Length + answer.Length + answertype_class_TTL.Length, address.Length);
        return packet;
    }

    private static byte[] EncodeMdnsName(string name)
    {
        string[] labels = name.Split('.');
        byte[] name_bytes = new byte[name.Length + 2];
        int index = 0;
        foreach (string label in labels)
        {
            name_bytes[index++] = (byte)label.Length;
            byte[] label_bytes = Encoding.UTF8.GetBytes(label);
            Buffer.BlockCopy(label_bytes, 0, name_bytes, index, label_bytes.Length);
            index += label_bytes.Length;
        }
        name_bytes[index] = 0; // End of the name
        return name_bytes;
    }

   

    static string GetLocalIPAddress()
    {
        // This is an interesting hack to obtain the local IP by faking a connection
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);

        socket.Connect("8.8.8.8", 65530);

        var endpoint = socket.LocalEndPoint as IPEndPoint;

        return endpoint.Address.ToString();
    }
}
//class Program
//{
//    static void Main(string[] args)
//    {
//        MdnsService service = new MdnsService("TestService", 8080);
//        service.Start();
//        Console.ReadKey();
//        service.Stop();
//    }
//}
