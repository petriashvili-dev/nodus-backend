using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nodus.Tests.Notifications;

/// <summary>Локальный SMTP по TCP: тестирует настоящий MailKit, включая envelope и DATA.</summary>
internal sealed class SmtpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(15));
    private readonly Task _run;
    private readonly int[] _recipientCodes;
    private readonly bool _silent;
    private readonly bool _dropOnQuit;
    private readonly bool _advertiseStartTls;
    public List<string> Commands { get; } = [];
    public List<string> Messages { get; } = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public SmtpTestServer(int[]? recipientCodes = null, bool silent = false, bool dropOnQuit = false, bool advertiseStartTls = true)
    {
        _recipientCodes = recipientCodes ?? [250];
        _silent = silent;
        _dropOnQuit = dropOnQuit;
        _advertiseStartTls = advertiseStartTls;
        _listener.Start();
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            foreach (var code in _recipientCodes)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                if (_silent)
                {
                    await Task.Delay(Timeout.Infinite, _shutdown.Token);
                    continue;
                }

                await writer.WriteLineAsync("220 test.nodus.test ESMTP");
                while (await reader.ReadLineAsync(_shutdown.Token) is { } command)
                {
                    Commands.Add(command);
                    if (command.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteAsync(_advertiseStartTls
                            ? "250-test.nodus.test\r\n250-8BITMIME\r\n250 STARTTLS\r\n"
                            : "250-test.nodus.test\r\n250 8BITMIME\r\n");
                    else if (command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync($"{code} recipient result recipient@gmail.com secret-server-detail");
                    else if (command == "DATA")
                    {
                        await writer.WriteLineAsync("354 Send data");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_shutdown.Token) is { } line && line != ".")
                            data.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
                        Messages.Add(data.ToString());
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (command == "QUIT")
                    {
                        if (!_dropOnQuit)
                            await writer.WriteLineAsync("221 Bye");
                        break;
                    }
                    else
                        await writer.WriteLineAsync("250 OK");
                }
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _run;
        _shutdown.Dispose();
    }
}
