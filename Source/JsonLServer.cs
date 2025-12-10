// RLBridge/Source/JsonLServer.cs
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RLBridge
{
    public sealed class JsonLServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Action<string> onLine;
        private TcpClient client;
        private NetworkStream stream;
        private Thread acceptThread, readThread;
        private volatile bool stop;

        // JSON serialization options to avoid encoding issues
        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public JsonLServer(IPAddress ip, int port, Action<string> onLine)
        {
            this.onLine = onLine;
            listener = new TcpListener(ip, port);
        }

        public void Start()
        {
            listener.Start();
            acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!stop)
            {
                try
                {
                    client = listener.AcceptTcpClient();
                    stream = client.GetStream();
                    readThread = new Thread(ReadLoop) { IsBackground = true };
                    readThread.Start();
                }
                catch { if (stop) break; }
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (!stop && client.Connected)
            {
                int n = 0;
                try { n = stream.Read(buf, 0, buf.Length); } catch { break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\n')
                    {
                        var line = sb.ToString();
                        sb.Clear();
                        if (!string.IsNullOrWhiteSpace(line))
                            onLine(line);
                    }
                    else sb.Append(c);
                }
            }
        }

        public bool TrySend(object obj)
        {
            if (stream == null) return false;
            try
            {
                var json = JsonSerializer.Serialize(obj, jsonOptions) + "\n";
                // Debug: log first 100 chars of message being sent
                var preview = json.Length > 100 ? json.Substring(0, 100) + "..." : json.TrimEnd('\n');
                Celeste.Mod.Logger.Log("RLBridge", $"[TCP SEND] {preview}");
                var data = Encoding.UTF8.GetBytes(json);
                stream.Write(data, 0, data.Length);
                stream.Flush();
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            stop = true;
            try { stream?.Dispose(); } catch { }
            try { client?.Close(); } catch { }
            try { listener?.Stop(); } catch { }
        }
    }
}
