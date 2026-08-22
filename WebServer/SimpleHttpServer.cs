using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Small HTTP/1.1 and WebSocket server that binds ordinary TCP sockets.
    /// Unlike HttpListener, exact non-loopback binds do not require HTTP.sys URLACLs.
    /// One request is served per HTTP connection; WebSocket connections stay open.
    /// </summary>
    internal sealed class SimpleHttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 16 * 1024 * 1024;
        private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(15);
        private const int MaxConcurrentConnections = 32;

        private readonly IReadOnlyList<IPAddress> _addresses;
        private readonly int _port;
        private readonly Func<SimpleHttpContext, CancellationToken, Task> _handler;
        private readonly Func<IPAddress, IPAddress, bool> _allowConnection;
        private readonly Action<string> _log;
        private readonly List<TcpListener> _listeners = new();
        private readonly List<Task> _acceptTasks = new();
        private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);

        public SimpleHttpServer(
            IReadOnlyList<IPAddress> addresses,
            int port,
            Func<IPAddress, IPAddress, bool> allowConnection,
            Func<SimpleHttpContext, CancellationToken, Task> handler,
            Action<string> log)
        {
            _addresses = addresses;
            _port = port;
            _allowConnection = allowConnection;
            _handler = handler;
            _log = log;
        }

        public bool IsListening => _listeners.Count > 0 && _listeners.All(listener => listener.Server.IsBound);

        public void Start(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var address in _addresses)
                {
                    var listener = new TcpListener(address, _port);
                    listener.Start();
                    _listeners.Add(listener);
                    _acceptTasks.Add(Task.Run(() => AcceptLoop(listener, cancellationToken), cancellationToken));
                }
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { }
            }
            _listeners.Clear();
            _acceptTasks.Clear();
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    var localAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                    var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    if (!_allowConnection(remoteAddress, localAddress))
                    {
                        client.Dispose();
                        continue;
                    }

                    if (!await _connectionSlots.WaitAsync(0, cancellationToken))
                    {
                        client.Dispose();
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleClient(client, remoteAddress, localAddress, cancellationToken);
                        }
                        finally
                        {
                            _connectionSlots.Release();
                        }
                    }, CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log($"Web server accept error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task HandleClient(
            TcpClient client,
            IPAddress remoteAddress,
            IPAddress localAddress,
            CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    await using var stream = client.GetStream();
                    using var requestReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestReadCts.CancelAfter(RequestReadTimeout);
                    var request = await ReadRequest(stream, remoteAddress, localAddress, requestReadCts.Token);
                    if (request == null) return;

                    var response = new SimpleHttpResponse();
                    var context = new SimpleHttpContext(request, response, stream);
                    await _handler(context, cancellationToken);

                    if (!context.WebSocketAccepted)
                        await WriteResponse(stream, response, cancellationToken);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
                catch (Exception ex)
                {
                    _log($"Web server client error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static async Task<SimpleHttpRequest?> ReadRequest(
            NetworkStream stream,
            IPAddress remoteAddress,
            IPAddress localAddress,
            CancellationToken cancellationToken)
        {
            using var headerBuffer = new MemoryStream();
            var terminatorState = 0;
            var oneByte = new byte[1];

            while (headerBuffer.Length < MaxHeaderBytes)
            {
                var read = await stream.ReadAsync(oneByte, cancellationToken);
                if (read == 0) return null;
                var value = oneByte[0];
                headerBuffer.WriteByte(value);

                terminatorState = (terminatorState, value) switch
                {
                    (0, (byte)'\r') => 1,
                    (1, (byte)'\n') => 2,
                    (2, (byte)'\r') => 3,
                    (3, (byte)'\n') => 4,
                    (_, (byte)'\r') => 1,
                    _ => 0,
                };
                if (terminatorState == 4) break;
            }

            if (terminatorState != 4)
                throw new InvalidDataException("HTTP request headers exceed the limit.");

            var headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length != 3 || !requestParts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid HTTP request line.");

            var headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Length == 0) break;
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                headers.Add(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }

            var contentLength = 0;
            var rawContentLength = headers["Content-Length"];
            if (!string.IsNullOrWhiteSpace(rawContentLength) &&
                (!int.TryParse(rawContentLength, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes))
            {
                throw new InvalidDataException("Invalid HTTP request body length.");
            }

            var body = new byte[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                if (read == 0) throw new EndOfStreamException("HTTP request body ended early.");
                offset += read;
            }

            var target = requestParts[1];
            if (!Uri.TryCreate(new Uri("http://localhost"), target, out var uri))
                throw new InvalidDataException("Invalid HTTP request target.");

            return new SimpleHttpRequest(
                requestParts[0].ToUpperInvariant(),
                uri,
                headers,
                body,
                remoteAddress,
                localAddress);
        }

        private static async Task WriteResponse(
            NetworkStream stream,
            SimpleHttpResponse response,
            CancellationToken cancellationToken)
        {
            var body = response.OutputStream.ToArray();
            if (response.StatusCode == 204) body = Array.Empty<byte>();

            var reason = Enum.IsDefined(typeof(HttpStatusCode), response.StatusCode)
                ? ((HttpStatusCode)response.StatusCode).ToString()
                : "Response";
            var header = new StringBuilder()
                .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
                .Append("Content-Length: ").Append(body.Length).Append("\r\n")
                .Append("Connection: close\r\n");

            if (!string.IsNullOrWhiteSpace(response.ContentType))
                header.Append("Content-Type: ").Append(SanitizeHeaderValue(response.ContentType)).Append("\r\n");

            foreach (var key in response.Headers.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key) ||
                    key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var values = response.Headers.GetValues(key) ?? Array.Empty<string>();
                foreach (var value in values)
                    header.Append(key).Append(": ").Append(SanitizeHeaderValue(value)).Append("\r\n");
            }

            header.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken);
            if (body.Length > 0)
                await stream.WriteAsync(body, cancellationToken);
        }

        private static string SanitizeHeaderValue(string value) =>
            value.Replace("\r", string.Empty, StringComparison.Ordinal)
                 .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    internal sealed class SimpleHttpContext
    {
        private readonly NetworkStream _stream;

        public SimpleHttpContext(SimpleHttpRequest request, SimpleHttpResponse response, NetworkStream stream)
        {
            Request = request;
            Response = response;
            _stream = stream;
        }

        public SimpleHttpRequest Request { get; }
        public SimpleHttpResponse Response { get; }
        public bool WebSocketAccepted { get; private set; }

        public async Task<WebSocket> AcceptWebSocketAsync()
        {
            if (!Request.IsWebSocketRequest)
                throw new InvalidOperationException("The request is not a WebSocket upgrade.");

            var key = Request.Headers["Sec-WebSocket-Key"];
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("Missing Sec-WebSocket-Key header.");

            var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {Convert.ToBase64String(acceptBytes)}\r\n\r\n";
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            WebSocketAccepted = true;
            return WebSocket.CreateFromStream(_stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        }
    }

    internal sealed class SimpleHttpRequest
    {
        public SimpleHttpRequest(
            string method,
            Uri url,
            NameValueCollection headers,
            byte[] body,
            IPAddress remoteAddress,
            IPAddress localAddress)
        {
            HttpMethod = method;
            Url = url;
            Headers = headers;
            InputStream = new MemoryStream(body, writable: false);
            RemoteEndPoint = new IPEndPoint(remoteAddress, 0);
            LocalEndPoint = new IPEndPoint(localAddress, 0);
            QueryString = ParseQuery(url.Query);
        }

        public string HttpMethod { get; }
        public Uri Url { get; }
        public NameValueCollection Headers { get; }
        public Stream InputStream { get; }
        public Encoding ContentEncoding => Encoding.UTF8;
        public IPEndPoint RemoteEndPoint { get; }
        public IPEndPoint LocalEndPoint { get; }
        public NameValueCollection QueryString { get; }
        public bool IsWebSocketRequest =>
            Headers["Upgrade"]?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true &&
            Headers["Connection"]?.Split(',').Any(value => value.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) == true;

        private static NameValueCollection ParseQuery(string query)
        {
            var values = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
                values.Add(key, value);
            }
            return values;
        }
    }

    internal sealed class SimpleHttpResponse
    {
        public int StatusCode { get; set; } = 200;
        public string? ContentType { get; set; }
        public long ContentLength64 { get; set; }
        public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MemoryStream OutputStream { get; } = new();

        public void AddHeader(string name, string value) => Headers.Add(name, value);
        public void Close() { }
    }
}
