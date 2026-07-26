using System.Net;
using System.Net.Sockets;
using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Facade.Smtp;

/// <summary>
/// The SMTP capture facade (G18b, ADR 0009): an opt-in ESMTP listener that accepts real mail from
/// real clients and hands each accepted message to <see cref="IMessageSink"/> as a
/// <see cref="MessageEnvelope"/> — protocol mock + capture in one. Speaks enough ESMTP for
/// mainstream clients (EHLO/HELO, MAIL FROM, RCPT TO, DATA with dot-unstuffing, RSET, NOOP, QUIT,
/// AUTH PLAIN/LOGIN accepted-but-unchecked). <b>The AUTH username names the tenant</b> — the SMTP
/// analog of the <c>X-Mockifyr-Tenant</c> header; without AUTH, mail lands in the default tenant.
/// No oracle exists for any of this (WireMock has no SMTP); validated by real-client self-tests.
/// </summary>
public sealed class SmtpCaptureServer(IMessageSink sink, int port) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private Task? _acceptLoop;

    /// <summary>The bound port (resolves an ephemeral request, i.e. port 0, after <see cref="Start"/>).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening and serving connections in the background.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                lock (_connections)
                {
                    _connections.RemoveAll(t => t.IsCompleted);
                    _connections.Add(ServeConnectionAsync(client));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var session = new SmtpSession(sink);
        await WriteAsync(stream, "220 mockifyr ESMTP ready");

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stopping.Token);
                if (line is null)
                {
                    return;
                }

                var (replies, action) = session.Handle(line);
                foreach (var reply in replies)
                {
                    await WriteAsync(stream, reply);
                }

                if (action == SmtpAction.ReadData)
                {
                    var data = await ReadDataAsync(reader);
                    await WriteAsync(stream, session.AcceptData(data));
                }
                else if (action == SmtpAction.Quit)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or the host is stopping — a mock server never turns that into noise.
        }
    }

    // DATA body: lines until the lone-dot terminator, with leading-dot unstuffing (RFC 5321 §4.5.2).
    private async Task<string> ReadDataAsync(StreamReader reader)
    {
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(_stopping.Token) ?? throw new IOException("Connection closed during DATA.");
            if (line == ".")
            {
                return data.ToString();
            }

            data.Append(line.StartsWith('.') ? line[1..] : line).Append("\r\n");
        }
    }

    private static Task WriteAsync(NetworkStream stream, string line) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n")).AsTask();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections.Append(_acceptLoop ?? Task.CompletedTask).Select(async t =>
        {
            try
            {
                await t;
            }
            catch (Exception)
            {
                // Connection teardown races are not shutdown failures.
            }
        }));
        _stopping.Dispose();
    }
}
