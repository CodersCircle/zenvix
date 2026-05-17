using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface ICommunicationServer
    {
        void Start(string pipeName);
        void Stop();
    }

    public class NamedPipeServer : ICommunicationServer
    {
        private CancellationTokenSource? _cts;
        private string? _pipeName;

        public void Start(string pipeName)
        {
            _pipeName = pipeName;
            _cts = new CancellationTokenSource();
            Task.Run(() => ListenAsync(_cts.Token));
            Log.Information("Named Pipe Server started: \\\\.\\pipe\\{PipeName}", pipeName);
        }

        public void Stop()
        {
            _cts?.Cancel();
            Log.Information("Named Pipe Server stopped.");
        }

        private async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrEmpty(_pipeName))
                    {
                        await Task.Delay(1000, token);
                        continue;
                    }
                    using (var server = new NamedPipeServerStream(_pipeName!, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await server.WaitForConnectionAsync(token);
                        Log.Debug("Client connected to Named Pipe.");

                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            var message = await reader.ReadLineAsync();
                            if (!string.IsNullOrEmpty(message))
                            {
                                Log.Information("Received IPC Message: {Message}", message);
                                // Here we would dispatch the message to the CommandBus
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in Named Pipe Server");
                    await Task.Delay(1000, token);
                }
            }
        }
    }
}
