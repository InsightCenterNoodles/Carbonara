using UnityEngine;
using PeterO.Cbor;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// TODO: Handle client disconnection better. For example table subscriptions, etc

/// <summary>
/// Message coming from the clients of a NOODLES session
/// </summary>
class IncomingMessage
{
    public Guid client_id;
    public CBORObject content;

    public IncomingMessage(Guid client_id, CBORObject content)
    {
        this.client_id = client_id;
        this.content = content;
    }
}

/// <summary>
/// A message going clients; optionally broadcasted
/// </summary>
public class OutgoingMessage
{
    public Guid? target;
    public CBORObject content;
    public bool enable;

    public OutgoingMessage(CBORObject content)
    {
        this.content = content;
        target = null;
        enable = false;
    }

    public OutgoingMessage(Guid to, CBORObject content)
    {
        this.content = content;
        target = to;
        enable = false;
    }
}

/// <summary>
/// Represents a client connected to the server
/// </summary>
class Client
{
    public Guid client_id;

    /// <summary>
    /// Active socket to communicate with the client
    /// </summary>
    public NWebSocket socket;

    /// <summary>
    /// Outgoing message queue. Another task is responsible for feeding the messages to the socket
    /// </summary>
    public AsyncQueue<byte[]> outgoing;

    public Client(Guid client_id, NWebSocket socket, AsyncQueue<byte[]> outgoing)
    {
        this.client_id = client_id;
        this.socket = socket;
        this.outgoing = outgoing;
    }
}

/// <summary>
/// A queue that can be waited on in async contexts
/// </summary>
/// <typeparam name="T">Type of queue value</typeparam>
public class AsyncQueue<T>
{
    /// <summary>
    /// Content queue
    /// </summary>
    private readonly ConcurrentQueue<T> _queue = new ();

    /// <summary>
    /// Gate for async readers
    /// </summary>
    private readonly SemaphoreSlim _signal = new (0);

    /// <summary>
    /// Add an item to the queue
    /// </summary>
    /// <param name="item">The item to add</param>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        _signal.Release(); // Signal that an item has been added
    }

    /// <summary>
    /// Extract an item from the queue. If empty, will pause the task until an item has been added.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>The next item in the queue</returns>
    public async Task<T> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken); // Wait for an item to be available

        if (_queue.TryDequeue(out var item))
        {
            return item;
        }

        throw new InvalidOperationException("Dequeue failed.");
    }
}

/// <summary>
/// A NOODLES server that replicates all child nodes
/// </summary>
public class NOOServer : MonoBehaviour {
    public static NOOServer Instance { get; private set; }

    /// <summary>
    /// Which hostname or IP to bind to
    /// </summary>
    public string Hostname = "localhost";

    /// <summary>
    /// Port to host the websocket for clients to connect to
    /// </summary>
    public int Port = 50000;

    /// <summary>
    /// Websocket server object
    /// </summary>
    private NWebSocketServer _socket_server;

    /// <summary>
    /// Token to halt the server
    /// </summary>
    private CancellationTokenSource _cancellation;

    /// <summary>
    /// Clients are recorded in this map of ID to client struct
    /// </summary>
    private ConcurrentDictionary<Guid, Client> _pending_clients = new();

    /// <summary>
    /// Clients are recorded in this map of ID to client struct
    /// </summary>
    private ConcurrentDictionary<Guid, Client> _active_clients = new();

    /// <summary>
    /// Incoming messages are placed here by listening tasks; these are to be processed by the server game object
    /// </summary>
    private ConcurrentQueue<IncomingMessage> _incomingMessages = new();

    /// <summary>
    /// A queue for outgoing messages; these will be processed by another task
    /// </summary>
    private AsyncQueue<OutgoingMessage> _outgoingMessages = new();

    private MdnsService _mdns_service = new("Carbonara", 50000); // TODO: Figure a better name

    /// <summary>
    /// The NOODLES state
    /// </summary>
    private NOOWorld _world;

    public NOOWorld World()
    {
        return _world;
    }

    private void Awake() {
        if (Instance == null) {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        } else {
            Destroy(gameObject);
        }
    }

    private void Start() {
        _cancellation = new CancellationTokenSource();

        // Set up asset server
        AssetServer.Instance.Init(Port + 1);
        Task.Run(() => AssetServer.Instance.StartAsync(_cancellation.Token));

        _socket_server = new NWebSocketServer(Port);
        _socket_server.Start();

        // Set up websocket
        var prefix = $"http://{Hostname}:{Port}/";

        Debug.Log($"WebSocket server started on {prefix}");
        Task.Run(() => ListenForConnections(_cancellation.Token));
        Task.Run(() => ProcessOutgoingMessages(_cancellation.Token));

        // Set up noodles world with outgoing message sink
        _world = new NOOWorld(_outgoingMessages);

        // Run MDNS
        _mdns_service.Start();

        OnTransformChildrenChanged();
    }

    /// <summary>
    /// Listen for new connections to the HTTP server
    /// </summary>
    /// <param name="token">Token to cancel listen task</param>
    /// <returns></returns>
    private async Task ListenForConnections(CancellationToken token) {
        Debug.Log($"Starting listen task");
        while (!token.IsCancellationRequested) {

            try
            {

                Debug.Log($"Waiting for connection...");
                var socket = await _socket_server.GetNextClient(token);
                Debug.Log($"New connection...");

                if (socket == null)
                {
                    continue;
                }

                _ = Task.Run(() => HandleWebSocketRequest(socket, token), token);
                
            }
            catch (Exception e)
            {
                Debug.LogError($"Unexpected exception: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Handle a new client from the websocket. This task sets up the connection and then constantly listens for new messages
    /// </summary>
    /// <param name="context"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task HandleWebSocketRequest(NWebSocket socket, CancellationToken token) {
        Debug.Log($"Starting client connection task");

        //var close_type = WebSocketCloseStatus.NormalClosure;

        // All clients have a unique ID
        var client_id = Guid.NewGuid();

        // Add client to client records
        var client = new Client(client_id, socket, new());
        _pending_clients.TryAdd(client_id, client);
        Debug.Log($"WebSocket client connected: {client_id}");

        // Setup a task to write messages to the client from the output queue
        _ = Task.Run(() => PerClientOutputTask(client, token));

        try
        {

            // As long as the socket is open, we listen for messages
            while (!token.IsCancellationRequested)
            {
                Debug.Log($"Await messages...{socket}");
                var content = await ReadMessageFrom(socket, client_id, token);

                // All messages should be an array
                if (content.Type != CBORType.Array)
                {
                    Debug.Log("Bad message");
                    // bad recv, we can just cut them off
                    break;
                }
                _incomingMessages.Enqueue(new IncomingMessage(client_id, content));
            }
        }

        catch (Exception e) {
            Debug.LogError($"WebSocket error for client {client_id}: {e.Message}, TRACE {e.StackTrace}");
        } finally {
            await socket.Close();
            _active_clients.TryRemove(client_id, out var this_client);
            _pending_clients.TryRemove(client_id, out this_client);
        }
    }

    /// <summary>
    /// Read a message from a socket for a client. This handles multipart websocket messages.
    /// </summary>
    /// <param name="socket"></param>
    /// <param name="identity"></param>
    /// <param name="token"></param>
    /// <returns>A CBOR array message; an undefined CBOR object if there is an error</returns>
    private static async Task<CBORObject> ReadMessageFrom(NWebSocket socket, Guid identity, CancellationToken token)
    {
        try
        {
            var mem_stream = new MemoryStream();

            // Read all parts. This could require a few loops if the message is fragmented
            while (!token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(token);

                switch (result.State)
                {
                    case NWebSocketType.MESSAGE:
                        mem_stream.Write(result.Payload, 0, result.Payload.Length);
                        break;
                    case NWebSocketType.CLOSING:
                        return CBORObject.Undefined;
                    case NWebSocketType.PING:
                        await socket.Pong();
                        break;
                }

                if (result.IsLast)
                {
                    break;
                }
            }

            if (mem_stream.Position == 0)
            {
                return CBORObject.Undefined;
            }

            // reset stream
            mem_stream.Seek(0, SeekOrigin.Begin);

            // decode
            var content = CBORObject.DecodeFromBytes(mem_stream.ToArray());

            // by the spec, this MUST be an array
            if (content.Type != CBORType.Array)
            {
                Debug.Log($"Bad message from: {identity}");
                return CBORObject.Undefined;
            }

            return content;
        }
        catch (Exception e)
        {
            Debug.LogError($"ReadMessageFrom error: {e.StackTrace}");
            return CBORObject.Undefined;
        }
    }

    /// <summary>
    /// Task to dole out messages from the global outgoing queue to individual clients
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task ProcessOutgoingMessages(CancellationToken token) {
        Debug.Log("Starting broadcast task");
        while (!token.IsCancellationRequested) {

            OutgoingMessage message;

            // Obtain a message from the global queue
            try
            {
                message = await _outgoingMessages.DequeueAsync(token);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Closing outgoing message reader: {ex}");
                return;
            }

            byte[] message_bytes;

            try
            {
                // We have to be careful here. Due to C# reference semantics,
                // This content could be stored somewhere else, and modified
                // in another thread
                message_bytes = message.content.EncodeToBytes();
             
            } catch (Exception ex)
            {
                Debug.LogError($"Unable to encode message: {ex}");
                return;
            }

            try
            { 
                if (message.target.HasValue)
                {
                    Debug.Log($"Posting message to: {message.target}");

                    if (message.enable)
                    {
                        if (_pending_clients.TryRemove(message.target.Value, out var inactive_client))
                        {
                            _active_clients[message.target.Value] = inactive_client;
                        } else
                        {
                            Debug.LogWarning("Unable to bring client to active status");
                        }
                    }

                    if (_active_clients.TryGetValue(message.target.Value, out var client))
                    {
                        client.outgoing.Enqueue(message_bytes);
                    }
                }
                else
                {
                    foreach (var client in _active_clients.Values)
                    {
                        client.outgoing.Enqueue(message_bytes);
                    }
                }

                
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error posting message: {ex}");
                return;
            }
        }
    }

    /// <summary>
    /// Takes messages from a per-client output queue and writes them to the socket
    /// </summary>
    /// <param name="client"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task PerClientOutputTask(Client client, CancellationToken token)
    {
        Debug.Log($"Starting output task for: {client.client_id}");
        while (!token.IsCancellationRequested)
        {
            try
            {
                var message = await client.outgoing.DequeueAsync(token);

                await client.socket.SendAsync(message, true);
                //Debug.Log($"Posted message to client: {message.Length}");
            }
            catch (Exception ex)
            {
                Debug.Log($"Closing watcher for {client.client_id}: {ex}");
                return;
            }
        }
    }


    private void Update() {
        // Handle any incoming messages
        while (_incomingMessages.TryDequeue(out var message))
        {
            var enumerator = message.content.Values.GetEnumerator();
            var from = message.client_id;

            while (enumerator.MoveNext())
            {
                var message_id = enumerator.Current.ToObject<int>();

                if (enumerator.MoveNext())
                {
                    var content = enumerator.Current;

                    HandleMessage(message_id, from, content);
                }
                else
                {
                    // Broken message stream!
                    break;
                }
            }

            //_world
        }
    }

    /// <summary>
    /// If children have changed, ensure that all have been given a watcher script
    /// </summary>
    private void OnTransformChildrenChanged()
    {
        // Check all children to see if they have been given a watcher

        Debug.Log("Children transform changed");

        for (var c_i = 0; c_i < transform.childCount; c_i++)
        {
            var child = transform.GetChild(c_i).gameObject;
            var watcher = child.GetComponent<NOOWatcher>();
            var vis = child.GetComponent<NOOVisibility>();

            if (watcher == null)
            {
                bool add = true;

                if (vis != null && vis.visibility == NOOVis.IGNORE)
                {
                    add = false;
                }

                if (add)
                {
                    // Add it to the child
                    child.AddComponent<NOOWatcher>();
                }
            }
        }
    }

    /// <summary>
    /// Handle a message from a client
    /// </summary>
    /// <param name="mid">integer message ID</param>
    /// <param name="content">message content</param>
    private void HandleMessage(int mid, Guid from, CBORObject content)
    {
        Debug.Log($"New message {mid}: {content}");
        switch (mid)
        {
            case 0:
                HandleIntroduction(from, content);
                break;
            case 1:
                HandleInvoke(from, content);
                break;
        }
    }

    /// <summary>
    /// Handle an introduction message
    /// </summary>
    /// <param name="content"></param>
    private void HandleIntroduction(Guid from, CBORObject content)
    {
        var client_name = content["client_name"].ToObject<String>();

        Debug.Log($"New client: {client_name}");

        // send messages

        //var buffs = _world.buffer_list.SplitDump();

        //foreach (var b in buffs)
        //{
        //    _outgoingMessages.Enqueue(new OutgoingMessage(from, b));
        //}

        var dump = _world.DumpToArray();
        dump.Add(35).Add(CBORObject.True);

        var msg = new OutgoingMessage(from, dump)
        {
            enable = true
        };

        _outgoingMessages.Enqueue(msg);
    }

    // TODO: Implement method handling
    private void HandleInvoke(Guid from, CBORObject content)
    {

    }

    private void OnApplicationQuit() {
        _cancellation.Cancel();
        _socket_server.Stop();
    }

}
