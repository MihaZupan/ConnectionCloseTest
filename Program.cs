using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;

string argsString = string.Join(' ', args);

if (args.Length == 0 || !int.TryParse(args[0], out int port))
{
    port = 5000;
}

var listenEndPoint = new IPEndPoint(IPAddress.Any, port);

if (args.Length < 2 || !int.TryParse(args[1], out int concurrentSockets))
{
    concurrentSockets = 1;
}

if (args.Length < 3 || !int.TryParse(args[2], out int asyncAccept))
{
    asyncAccept = 1;
}

Console.WriteLine($"{nameof(GCSettings.IsServerGC)}={GCSettings.IsServerGC}");
Console.WriteLine($"{nameof(listenEndPoint)}={listenEndPoint}");
Console.WriteLine($"{nameof(concurrentSockets)}={concurrentSockets}");
Console.WriteLine($"{nameof(asyncAccept)}={asyncAccept}");

Task acceptLoopTask = Task.Run(() => BenchmarkApp.AcceptLoop(listenEndPoint, concurrentSockets, asyncAccept != 0));
Console.WriteLine("Application started.");
await acceptLoopTask;

static class BenchmarkApp
{
    private static readonly byte[] s_serverResponse = "HTTP/1.1 200 OK\r\nContent-Length:11\r\nConnection: close\r\n\r\nHello world"u8.ToArray();
    private static int s_requestCounter;
    private static readonly ConcurrentQueue<byte[]> s_bufferQueue = new();

    public static async Task AcceptLoop(IPEndPoint listenEndPoint, int concurrentSockets, bool asyncAccept)
    {
        Task[] tasks = new Task[concurrentSockets];
        for (int i = 0; i < tasks.Length; i++)
        {
            int socketNum = i + 1;

            if (asyncAccept)
            {
                tasks[i] = AcceptLoopCore(listenEndPoint, asyncAccept, socketNum);
            }
            else
            {
                tasks[i] = Task.Factory.StartNew(() => AcceptLoopCore(listenEndPoint, asyncAccept, socketNum).Wait(), TaskCreationOptions.LongRunning);
            }
        }

        await Task.WhenAll(tasks);
    }

    private static async Task AcceptLoopCore(IPEndPoint listenEndPoint, bool asyncAccept, int i)
    {
        try
        {
            using var acceptSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                byte[] reuseAddressVal = new byte[] { 1 };
                acceptSocket.SetRawSocketOption((int)SocketOptionLevel.Socket, (int)SocketOptionName.ReuseAddress, reuseAddressVal);
                object? getVal = acceptSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress);
                Console.WriteLine($"Socket {i} reuseAddress: {getVal}");
            }
            else
            {
                byte[] reusePortVal = new byte[] { 1, 0, 0, 0 };
                acceptSocket.SetRawSocketOption(1, 0xf, reusePortVal);
                byte[] valBuffer = new byte[16];
                int valLength = acceptSocket.GetRawSocketOption(1, 0xf, valBuffer);
                Console.WriteLine($"Socket {i} reusePort: {BitConverter.ToString(valBuffer.AsSpan(0, valLength).ToArray())}");
            }

            acceptSocket.Bind(listenEndPoint);
            acceptSocket.Listen(512);
            Console.WriteLine($"Socket {i} listening ...");

            while (true)
            {
                Socket clientSocket = asyncAccept
                    ? await acceptSocket.AcceptAsync()
                    : acceptSocket.Accept();

                int requestCounter = Interlocked.Increment(ref s_requestCounter);
                bool trace = requestCounter % 10_000 == 0;
                if (trace)
                {
                    Console.WriteLine($"Request {requestCounter} on connection {i}");
                    //await Task.Delay(1000);
                }

                _ = Task.Run(() => ServeRequestAsync(clientSocket, trace));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Accept error on socket {i}: {ex}");
        }
    }

    private static async Task ServeRequestAsync(Socket clientSocket, bool trace)
    {
        try
        {
            using var stream = new NetworkStream(clientSocket, ownsSocket: true);

            if (!s_bufferQueue.TryDequeue(out var buffer))
            {
                buffer = new byte[1024];
            }

            int read = await stream.ReadAsync(buffer);
            if (trace)
            {
                Console.WriteLine($"Read={read}");
            }

            s_bufferQueue.Enqueue(buffer);

            await stream.WriteAsync(s_serverResponse);

            clientSocket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
}