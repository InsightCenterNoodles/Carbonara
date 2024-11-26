using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Enum representing the possible types of a WebSocket message
/// </summary>
public enum NWebSocketType
{
    MESSAGE,    // Normal message (text or binary)
    CLOSING,    // Connection close message
    PING,       // Ping message
}

/// <summary>
/// Class to represent a WebSocket message. Contains the payload, whether it's the last message, and the message state.
/// </summary>
public class NWebSocketMessage
{
    public byte[] Payload { get; set; }    // The actual payload (message content)
    public bool IsLast { get; set; }       // Flag indicating if this is the last part of the message
    public NWebSocketType State { get; set; } // The current type of the message (e.g., MESSAGE, CLOSING)
}

/// <summary>
/// Custom exception for handling cases where a WebSocket message is too large
/// </summary>
public class NWSTooLargeException : Exception
{
    public NWSTooLargeException() { }
    public NWSTooLargeException(string message) : base(message) { }
    public NWSTooLargeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Custom exception for handling unknown WebSocket message opcodes
/// </summary>
public class NWSUnknownMessageException : Exception
{
    public NWSUnknownMessageException() { }
    public NWSUnknownMessageException(string message) : base(message) { }
    public NWSUnknownMessageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Class to represent a WebSocket connection. Handles WebSocket handshake, message receiving, and sending.
/// </summary>
public class NWebSocket
{
    private readonly TcpClient _client;  // The underlying TCP client
    private readonly Socket _socket;    // The socket for sending/receiving data
    private bool _is_closed = false;    // Flag indicating if the WebSocket is closed
    private int _max_packet = 0;        // The maximum packet size allowed for sending messages

    /// <summary>
    /// Constructor for initializing the WebSocket with an existing TcpClient
    /// </summary>
    /// <param name="client">TCP stream to manage</param>
    public NWebSocket(TcpClient client)
    {
        _client = client;
        _socket = client.Client;  // Get the socket from the TcpClient
        _socket.NoDelay = true;   // Disable Nagle's algorithm for low-latency transmission
        _max_packet = _socket.SendBufferSize;  // Set the max packet size based on the socket's send buffer

        // We are noticing that messages larger than that buffer get dropped, which is odd. So we have this limit to break things up.
    }

    /// <summary>
    /// Method to perform the WebSocket handshake, including sending the appropriate response.
    /// </summary>
    /// <param name="token"></param>
    /// <returns>true if the handshake is successful</returns>
    public async Task<bool> PerformHandshakeAsync(CancellationToken token)
    {
        // Allocate a buffer to read the incoming request
        var buffer = new byte[1024];
        int bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);
        var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check if the request contains the "Upgrade: websocket" header
        if (!request.Contains("Upgrade: websocket")) return false;

        // Extract the WebSocket key from the request
        string key = request.Split(new[] { "Sec-WebSocket-Key: " }, StringSplitOptions.None)[1]
                            .Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Trim();

        // Generate the response key by hashing the WebSocket key with a specific magic string
        string responseKey = Convert.ToBase64String(
            System.Security.Cryptography.SHA1.Create().ComputeHash(
                Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        // Build the WebSocket handshake response
        var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                       $"Connection: Upgrade\r\n" +
                       $"Upgrade: websocket\r\n" +
                       $"Sec-WebSocket-Accept: {responseKey}\r\n\r\n";

        // Convert the response to bytes and send it back to the client
        var response_bytes = Encoding.UTF8.GetBytes(response);
        await _socket.SendAsync(response_bytes, SocketFlags.None, token);

        return true;
    }

    /// <summary>
    /// Method to receive a WebSocket message, handling various opcodes and parsing payload data.
    /// </summary>
    /// <param name="token"></param>
    /// <returns>A new message from the socket</returns>
    public async Task<NWebSocketMessage> ReceiveAsync(CancellationToken token)
    {
        byte[] header = new byte[2];  // The first 2 bytes represent the frame header
        await _socket.ReceiveAsync(header, SocketFlags.None, token);

        bool fin = (header[0] & 0b10000000) != 0;    // FIN bit: Indicates if this is the last frame
        int opcode = header[0] & 0b00001111;         // Opcode: Specifies the type of message (e.g., text, binary, etc.)
        bool masked = (header[1] & 0b10000000) != 0;  // Mask bit: Indicates if the payload is masked
        int provided_length = header[1] & 0b01111111; // Payload length: The first 7 bits of the second byte
        int payload_length = provided_length;

        // Handle extended payload lengths (126 or 127)
        if (provided_length == 126)
        {
            byte[] extended_length_bytes = new byte[2];
            await _socket.ReceiveAsync(extended_length_bytes, SocketFlags.None, token);
            payload_length = ((extended_length_bytes[0] << 8) | extended_length_bytes[1]);
        }
        else if (provided_length == 127)
        {
            byte[] extended_length_bytes = new byte[8];
            await _socket.ReceiveAsync(extended_length_bytes, SocketFlags.None, token);

            // Reverse byte order for big-endian values
            for (int i = 0; i < extended_length_bytes.Length / 2; i++)
            {
                byte tmp = extended_length_bytes[i];
                extended_length_bytes[i] = extended_length_bytes[extended_length_bytes.Length - i - 1];
                extended_length_bytes[extended_length_bytes.Length - i - 1] = tmp;
            }

            payload_length = (int)BitConverter.ToUInt64(extended_length_bytes, 0);
        }

        // Ensure the payload length isn't suspiciously large
        if (payload_length > 1E8)
        {
            throw new NWSTooLargeException($"Payload is {payload_length}");
        }

        // Read the masking key if present
        byte[] masking_key = null;
        if (masked)
        {
            masking_key = new byte[4];
            await _socket.ReceiveAsync(masking_key, SocketFlags.None, token);
        }

        // Read the payload data
        byte[] payload_data = new byte[payload_length];
        await _socket.ReceiveAsync(payload_data, SocketFlags.None, token);

        // Unmask the payload if it was masked
        if (masked)
        {
            for (int i = 0; i < payload_length; i++)
            {
                payload_data[i] = (byte)(payload_data[i] ^ masking_key[i % 4]);
            }
        }

        // Handle the message based on the opcode
        switch (opcode)
        {
            case 0x1: // Text frame (we treat text here as binary)
            case 0x2: // Binary frame
                return new NWebSocketMessage
                {
                    Payload = payload_data,
                    IsLast = fin,
                    State = NWebSocketType.MESSAGE,
                };
            case 0x8: // Connection close
                return new NWebSocketMessage
                {
                    Payload = payload_data,
                    IsLast = fin,
                    State = NWebSocketType.CLOSING,
                };
            case 0x9: // Ping
                return new NWebSocketMessage
                {
                    Payload = payload_data,
                    IsLast = fin,
                    State = NWebSocketType.PING,
                };
            case 0xA: // Pong (no action needed)
                break;
            default:
                throw new NWSUnknownMessageException($"Unknown opcode: {opcode}");
        }

        throw new NWSUnknownMessageException("Unknown message type.");
    }

    /// <summary>
    /// Method to send a WebSocket message (could be split into multiple chunks if the message is large)
    /// </summary>
    /// <param name="message">Binary content to send</param>
    /// <param name="last_message">False if you intend to stream more content as part of one message</param>
    /// <returns></returns>
    public async Task SendAsync(byte[] message, bool last_message = true)
    {
        // Split the message into chunks if it exceeds the max packet size
        if (message.Length > _max_packet)
        {
            int offset = 0;

            while (offset < message.Length)
            {
                int chunk_size = Math.Min(_max_packet, message.Length - offset);
                var chunk = new Memory<byte>(message, offset, chunk_size);

                // Send the frame header for this chunk
                await SendChunkHeaderAsync(chunk, last_message && (offset + chunk_size == message.Length));

                // Send the actual chunk
                await _socket.SendAsync(chunk, SocketFlags.None);

                offset += chunk_size;
            }
        }
        else
        {
            // No need to split, send the entire message in one go
            await SendChunkHeaderAsync(message, last_message);
            await _socket.SendAsync(message, SocketFlags.None);
        }
    }

    /// <summary>
    /// Helper method to send the frame header for a chunk
    /// </summary>
    /// <param name="chunk">Chunk of content to send</param>
    /// <param name="last_message">Control fin bit</param>
    /// <returns></returns>
    private async Task SendChunkHeaderAsync(Memory<byte> chunk, bool last_message)
    {
        byte fin_bit = (byte)(last_message ? 0x80 : 0x00);  // Set the FIN bit if it's the final chunk
        byte opcode = 0x02;  // Binary frame opcode
        byte first_byte = (byte)(fin_bit | opcode);

        byte[] frame_header;

        if (chunk.Length <= 125)
        {
            frame_header = new byte[] { first_byte, (byte)chunk.Length };
        }
        else if (chunk.Length <= 65535)
        {
            // Extended payload length (2 bytes)
            frame_header = new byte[4];
            frame_header[0] = first_byte;
            frame_header[1] = 126;
            frame_header[2] = (byte)((chunk.Length >> 8) & 0xFF);
            frame_header[3] = (byte)(chunk.Length & 0xFF);
        }
        else
        {
            // Extended payload length (8 bytes)
            frame_header = new byte[10];
            frame_header[0] = first_byte;
            frame_header[1] = 127;
            for (int i = 0; i < 8; i++)
            {
                frame_header[9 - i] = (byte)((chunk.Length >> (8 * i)) & 0xFF);
            }
        }

        // Send the header first
        await _socket.SendAsync(frame_header, SocketFlags.None);
    }

    /// <summary>
    /// Sends a Pong message as a response to a Ping. Note, you have to do this yourself! Automatic replies to Pings are not part of this class!
    /// </summary>
    /// <returns></returns>
    public async Task Pong()
    {
        var header = new byte[] { 0x8a, 0 }; // Opcode for Pong message

        await _socket.SendAsync(header, SocketFlags.None);
    }

    /// <summary>
    /// Method to close the WebSocket connection somewhat gracefully
    /// </summary>
    /// <returns></returns>
    public async Task Close()
    {
        // Prevent closing multiple times
        if (_is_closed) return;
        _is_closed = true;

        // Send a close frame with optional status code (1000 = Normal Closure)
        byte[] close_frame = new byte[] { 0x88, 0x02, 0x03, 0xE8 }; // FIN + Close opcode (0x8), 2-byte payload (1000 status code)

        await _socket.SendAsync(close_frame, SocketFlags.None);

        // Close the underlying socket and stream
        _socket.Close();
    }
}

/// <summary>
/// Class to represent the WebSocket server. Manages incoming connections and handshakes.
/// </summary>
public class NWebSocketServer
{
    private TcpListener _listener;  // Listener for incoming TCP connections
    private readonly int _port;     // Port to listen on

    /// <summary>
    /// Constructor to initialize the server with a specific port. By default, the server is bound to all network interfaces.
    /// </summary>
    /// <param name="port">Port to bind to</param>
    public NWebSocketServer(int port)
    {
        _port = port;
    }

    /// <summary>
    /// Starts the server and listens for incoming connections
    /// </summary>
    /// <returns></returns>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"WebSocket server started on port {_port}");
    }

    /// <summary>
    /// Accepts a new client connection, performs the WebSocket handshake, and returns a new NWebSocket instance
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<NWebSocket> GetNextClient(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync();
            var ws = new NWebSocket(client);

            // Perform the WebSocket handshake. If it fails, close the client connection and wait for the next connection.
            if (!await ws.PerformHandshakeAsync(cancellationToken))
            {
                client.Close();
                continue;
            }

            return ws;
        }

        return null;
    }

    /// <summary>
    /// Stops the server
    /// </summary>
    public void Stop()
    {
        Console.WriteLine($"WebSocket server stopped");
        _listener?.Stop();
    }
}
