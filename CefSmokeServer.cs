using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MBrowser;

/// <summary>A disposable loopback-only page for deterministic CEF smoke tests.</summary>
internal sealed class CefSmokeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _response;
    internal string Url { get; }

    internal CefSmokeServer(bool loginPage = false)
    {
        string html = loginPage
            ? "<!doctype html><html><head><title>MBrowser credential smoke</title></head><body>"
              + "<form action='/signin' method='post'><label>User <input type='text' autocomplete='username' name='username'></label>"
              + "<label>Password <input type='password' autocomplete='current-password' name='password'></label>"
              + "<button type='submit'>Sign in</button></form></body></html>"
            : "<!doctype html><html><head><title>MBrowser CEF smoke</title></head><body><p>CEF smoke</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        _response = [.. header, .. body];
        _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            _ = RespondAsync(client);
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] request = new byte[2048];
                int received = await stream.ReadAsync(request, _stop.Token);
                if (received == 0) return;
                await stream.WriteAsync(_response, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
