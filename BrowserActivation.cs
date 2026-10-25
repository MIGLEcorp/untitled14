using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace MBrowser;

/// <summary>Forwards shell links/files to the existing window through a current-user-only pipe.</summary>
internal sealed class BrowserActivation : IDisposable
{
    internal static string PipeName => "MBrowser-Open-" + WindowsIdentity.GetCurrent().User!.Value;
    private readonly CancellationTokenSource _stop = new();
    internal Task Listening { get; }
    internal BrowserActivation(Action<string[]> receive, string? pipeName = null)
    {
        Listening = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(3000);
                    byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, deadline.Token);
                    int length = BitConverter.ToInt32(header);
                    if (length is < 2 or > 65536) continue;
                    byte[] bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, deadline.Token);
                    string[] values = JsonSerializer.Deserialize<string[]>(bytes) ?? [];
                    if (values.Length <= 16 && values.All(value => value is not null && value.Length <= 32768)) receive(values);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
            }
        });
    }
    internal static bool Send(string[] targets, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(3000);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint id)) AllowSetForegroundWindow(id);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(targets);
            if (bytes.Length > 65536) return false;
            pipe.Write(BitConverter.GetBytes(bytes.Length)); pipe.Write(bytes); pipe.Flush(); return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() => _stop.Cancel();
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
