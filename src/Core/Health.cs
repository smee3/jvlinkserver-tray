using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace JVLinkServerTray
{
    public enum HealthKind { Healthy, Answered, Unreachable }

    public sealed class HealthReport
    {
        public HealthKind Kind { get; set; }
        public int HttpStatus { get; set; }
        public string Status { get; set; }
        public long RestartCount { get; set; }
        public string LastRestartReason { get; set; }
        public string Error { get; set; }

        public HealthReport() { RestartCount = -1; }

        public static HealthReport Unreachable(string error)
        {
            return new HealthReport { Kind = HealthKind.Unreachable, HttpStatus = 0, Error = error };
        }
    }

    public static class HealthParser
    {
        public static HealthReport Parse(int httpStatus, string body)
        {
            var r = new HealthReport { Kind = HealthKind.Answered, HttpStatus = httpStatus, Status = "http " + httpStatus };
            try
            {
                if (string.IsNullOrEmpty(body)) return r;
                var root = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (root == null) return r;
                object st;
                if (root.TryGetValue("status", out st) && st is string) r.Status = (string)st;
                var comps = Child(root, "components");
                var worker = comps == null ? null : Child(comps, "worker_process");
                if (worker != null)
                {
                    object rc, rr;
                    if (worker.TryGetValue("restart_count", out rc)) r.RestartCount = ToLong(rc);
                    if (worker.TryGetValue("last_restart_reason", out rr)) r.LastRestartReason = rr as string;
                }
                if (httpStatus == 200 && r.Status == "healthy") r.Kind = HealthKind.Healthy;
            }
            catch (Exception)
            {
            }
            return r;
        }

        static Dictionary<string, object> Child(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        static long ToLong(object v)
        {
            try
            {
                if (v is int) return (int)v;
                if (v is long) return (long)v;
                if (v is decimal) return decimal.ToInt64((decimal)v);
                if (v is double) return (long)(double)v;
            }
            catch (Exception) { }
            return -1;
        }
    }
}
