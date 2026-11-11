using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

// A stand-in for JVLinkServer.exe, used only by the tests.
// Supervisor mode (--port <n>): starts itself again as "--mode worker --port 0" without a window,
// then answers GET /health and POST /shutdown on 127.0.0.1:<n>.
// Options: --answer healthy|503, --hang-health, --big-health (a healthy body of about 100 KB), --no-listen,
// --ignore-shutdown. Unknown arguments are ignored.
namespace JVLinkServerTray.Fake
{
    public static class FakeJVLinkServer
    {
        const string HealthyBody = "{\"status\":\"healthy\",\"components\":{\"worker_process\":{\"restart_count\":0,\"last_restart_reason\":null}}}";
        const string StartingBody = "{\"status\":\"starting\",\"components\":{\"worker_process\":{\"restart_count\":0,\"last_restart_reason\":null}}}";

        static string _answer = "healthy";
        static bool _hangHealth, _bigHealth, _ignoreShutdown;
        static Process _worker;
        static readonly object Gate = new object();

        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                try { Console.Error.WriteLine("FakeJVLinkServer: " + e.Message); } catch (Exception) { }
                StopWorker();
                return 1;
            }
        }

        static int Run(string[] args)
        {
            int port = 0;
            bool worker = false, noListen = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--port" && next != null) { int.TryParse(next, out port); i++; }
                else if (a == "--mode" && next != null) { worker = next == "worker"; i++; }
                else if (a == "--answer" && next != null) { _answer = next; i++; }
                else if (a == "--hang-health") _hangHealth = true;
                else if (a == "--big-health") _bigHealth = true;
                else if (a == "--no-listen") noListen = true;
                else if (a == "--ignore-shutdown") _ignoreShutdown = true;
            }

            if (worker)
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            // Start the worker before opening the port so the child does not inherit the listening socket.
            StartWorker();
            if (noListen || port <= 0)
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.ExclusiveAddressUse = false;
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                var t = new Thread(Handle);
                t.IsBackground = true;
                t.Start(c);
            }
        }

        static void StartWorker()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            var psi = new ProcessStartInfo(exe, "--mode worker --port 0");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            lock (Gate) { _worker = Process.Start(psi); }
        }

        static void StopWorker()
        {
            lock (Gate)
            {
                if (_worker == null) return;
                try
                {
                    if (!_worker.HasExited)
                    {
                        _worker.Kill();
                        _worker.WaitForExit(5000);
                    }
                }
                catch (Exception) { }
            }
        }

        static void Handle(object state)
        {
            var c = (TcpClient)state;
            try
            {
                c.ReceiveTimeout = 10000;
                c.SendTimeout = 10000;
                NetworkStream s = c.GetStream();
                string method, path;
                if (!ReadRequest(s, out method, out path)) return;

                if (method == "GET" && path == "/health")
                {
                    if (_hangHealth)
                    {
                        Thread.Sleep(Timeout.Infinite);   // accepted, never answered
                        return;
                    }
                    if (_bigHealth) Respond(s, 200, "OK", "{\"status\":\"healthy\",\"pad\":\"" + new string('x', 100000) + "\"}");
                    else if (_answer == "503") Respond(s, 503, "Service Unavailable", StartingBody);
                    else Respond(s, 200, "OK", HealthyBody);
                }
                else if (method == "POST" && path == "/shutdown")
                {
                    Respond(s, 200, "OK", "{\"status\":\"shutting_down\"}");
                    FinishGracefully(c, s);
                    if (!_ignoreShutdown)
                    {
                        StopWorker();
                        Environment.Exit(0);
                    }
                }
                else
                {
                    Respond(s, 404, "Not Found", "{\"error\":\"not found\"}");
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try { c.Close(); } catch (Exception) { }
            }
        }

        // Reads the request line, the headers and a Content-Length body (if any).
        static bool ReadRequest(NetworkStream s, out string method, out string path)
        {
            method = null; path = null;
            var head = new MemoryStream();
            int b, matched = 0;
            byte[] end = { 13, 10, 13, 10 };
            while (matched < 4)
            {
                b = s.ReadByte();
                if (b < 0) return false;
                head.WriteByte((byte)b);
                if (head.Length > 65536) return false;
                if (b == end[matched]) matched++;
                else matched = b == 13 ? 1 : 0;
            }
            string text = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0];
            path = first[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            int length = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(lines[i].Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(lines[i].Substring(colon + 1).Trim(), out length);
            }
            if (length > 1048576) return false;
            var buf = new byte[4096];
            while (length > 0)
            {
                int n = s.Read(buf, 0, Math.Min(buf.Length, length));
                if (n <= 0) return false;
                length -= n;
            }
            return true;
        }

        static void Respond(NetworkStream s, int code, string reason, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + bytes.Length + "\r\n"
                + "Connection: close\r\n"
                + "\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        // Sends FIN and waits (up to 2 s) for the client to close, so the answer is delivered before we exit.
        static void FinishGracefully(TcpClient c, NetworkStream s)
        {
            try
            {
                c.Client.Shutdown(SocketShutdown.Send);
                c.ReceiveTimeout = 2000;
                var buf = new byte[256];
                while (s.Read(buf, 0, buf.Length) > 0) { }
            }
            catch (Exception)
            {
            }
        }
    }
}
