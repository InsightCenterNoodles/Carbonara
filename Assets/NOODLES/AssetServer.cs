using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

/// <summary>
/// A handle to a single HTTP stored asset
/// </summary>
public class Asset
{
    /// <summary>
    /// The ID of this asset
    /// </summary>
    public string Identity { get; }

    /// <summary>
    /// The port this asset is being served on
    /// </summary>
    /// Todo: Can be removed
    public int Port { get; }

    /// <summary>
    /// Stores a new asset on the HTTP server
    /// </summary>
    /// <param name="data">content to store in the asset</param>
    public Asset(byte[] data)
    {
        Identity = Guid.NewGuid().ToString();
        Port = AssetServer.Instance.Port();
        AssetServer.Instance.Install(Identity, data);
    }

    ~Asset()
    {
        Debug.Log("Dropping asset handle");
        AssetServer.Instance.Remove(Identity);
    }
}

/// <summary>
/// An HTTP server for NOODLES binary assets
/// </summary>
class AssetServer
{
    /// <summary>
    /// Global server instance
    /// </summary>
    public static AssetServer Instance = new();

    /// <summary>
    /// HTTP server
    /// </summary>
    private HttpListener _listener;

    /// <summary>
    /// Storage for assets, keyed by ID
    /// </summary>
    private ConcurrentDictionary<string, byte[]> _blobStorage;

    /// <summary>
    /// Port to serve on
    /// </summary>
    private int _port;

    public AssetServer()
    {
    }

    /// <summary>
    /// Launch server. Call only once!
    /// </summary>
    /// <param name="port">Port to serve on</param>
    public void Init(int port)
    {
        _port = port;
        _listener = new();
        _listener.Prefixes.Add($"http://*:{_port}/");
        _blobStorage = new();
    }

    public int Port() { return _port;  }

    /// <summary>
    /// A task to handle requests
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener.Start();
            Debug.Log($"Server listening on http://0.0.0.0:{_port}/");
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), cancellationToken);
                }
                catch (Exception ex)
                {
                    Debug.Log($"Error: {ex.Message}");
                    break;
                }
            }
            _listener.Stop();

        } catch (Exception ex)
        {
            Debug.Log($"Error asset server main loop: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a blob to the http server
    /// </summary>
    /// <param name="id"></param>
    /// <param name="data"></param>
    public void Install(string id, byte[] data)
    {
        Debug.Log($"Installing {id} -> {data.Length}");
        _blobStorage.TryAdd(id, data);
    }

    /// <summary>
    /// Remove an asset from the server
    /// </summary>
    /// <param name="id"></param>
    public void Remove(string id)
    {
        Debug.Log($"Removing {id}");
        _blobStorage.TryRemove(id, out var _);
    }

    /// <summary>
    /// Logic for a single request
    /// </summary>
    /// <param name="context"></param>
    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var asset_id = request.Url.AbsolutePath.TrimStart(new char[] { '/' });

        Debug.Log($"Handling request for {asset_id}");

        try
        {

            // Set response headers for CORS
            response.AddHeader("Access-Control-Allow-Origin", "*");

            // Handle the OPTIONS pre-flight request
            if (request.HttpMethod == "OPTIONS")
            {
                response.AddHeader("Access-Control-Max-Age", "3600");
                response.StatusCode = 200;
                response.Close();
                Debug.Log("Handled OPTIONS request.");
                return;
            }

            if (request.HttpMethod == "GET" && _blobStorage.TryGetValue(asset_id, out var blob))
            {
                response.ContentType = "application/octet-stream";
                response.ContentLength64 = blob.Length;
                response.OutputStream.Write(blob, 0, blob.Length);
                Debug.Log("Blob found.");
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                Debug.Log("Blob not found.");
            }

            response.Close();
        }
        catch (Exception ex)
        {
            Debug.Log($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop the server
    /// </summary>
    public void Stop()
    {
        _listener.Stop();
    }
}