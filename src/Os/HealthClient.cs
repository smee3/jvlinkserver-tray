using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace JVLinkServerTray
{
    // Talks to the server on 127.0.0.1:<port>. Never throws; every call is bounded by its timeout.
    public sealed class HealthClient
    {
        // A hard bound on top of HttpWebRequest.Timeout: the request is aborted this long after timeoutMs.
        const int AbortSlackMs = 500;
        // /health answers are a few hundred bytes.
        const int MaxBodyChars = 64 * 1024;

        readonly int _port;

        public HealthClient(int port)
        {
            _port = port;
        }

        public HealthReport Get(int timeoutMs)
        {
            try
            {
                int status;
                string body, error;
                if (Send("GET", "/health", timeoutMs, out status, out body, out error))
                    return HealthParser.Parse(status, body);
                return HealthReport.Unreachable(error);
            }
            catch (Exception e)
            {
                return HealthReport.Unreachable(e.Message);
            }
        }

        // POST /shutdown with an empty body. True when the server gave any HTTP answer.
        public bool Shutdown(int timeoutMs)
        {
            try
            {
                int status;
                string body, error;
                return Send("POST", "/shutdown", timeoutMs, out status, out body, out error);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // True when an HTTP answer (any status) came back; status and body are then set.
        bool Send(string method, string path, int timeoutMs, out int status, out string body, out string error)
        {
            status = 0;
            body = null;
            error = null;
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _port + path);
                req.Method = method;
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.ServicePoint.Expect100Continue = false;
                if (method == "POST") req.ContentLength = 0;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            long due = (long)timeoutMs + AbortSlackMs;
            if (timeoutMs < 0 || due > int.MaxValue) due = Timeout.Infinite;
            var guard = new Timer(delegate
            {
                try { req.Abort(); } catch (Exception) { }
            }, null, (int)due, Timeout.Infinite);

            HttpWebResponse resp = null;
            try
            {
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException e)
                {
                    resp = e.Response as HttpWebResponse;
                    if (resp == null)
                    {
                        if (e.Response != null) e.Response.Close();
                        error = e.Message;
                        return false;
                    }
                }
                status = (int)resp.StatusCode;
                body = ReadBody(resp);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                guard.Dispose();
                if (resp != null)
                {
                    try { resp.Close(); } catch (Exception) { }
                }
            }
        }

        // The status line already arrived, so a body that cannot be read still counts as an answer.
        // So does a body over MaxBodyChars: it is not read on, and an empty body is never healthy.
        static string ReadBody(HttpWebResponse resp)
        {
            try
            {
                using (Stream s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    var buf = new char[MaxBodyChars + 1];
                    int n = 0, got;
                    while (n < buf.Length && (got = r.Read(buf, n, buf.Length - n)) > 0) n += got;
                    return n > MaxBodyChars ? "" : new string(buf, 0, n);
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
