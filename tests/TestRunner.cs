using System;
using System.Collections.Generic;
using System.Reflection;

namespace JVLinkServerTray.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void Equal<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException((what ?? "value") + ": expected <" + Show(expected) + "> but was <" + Show(actual) + ">");
        }
        public static void True(bool cond, string what)
        {
            if (!cond) throw new AssertionException(what + ": expected true");
        }
        public static void False(bool cond, string what)
        {
            if (cond) throw new AssertionException(what + ": expected false");
        }
        public static void Null(object o, string what)
        {
            if (o != null) throw new AssertionException(what + ": expected null but was <" + Show(o) + ">");
        }
        public static void NotNull(object o, string what)
        {
            if (o == null) throw new AssertionException(what + ": expected not null");
        }
        private static string Show(object o) { return o == null ? "null" : o.ToString(); }
    }

    public static class TestRunner
    {
        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            int passed = 0, failed = 0;
            foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.GetCustomAttributes(typeof(TestAttribute), false).Length == 0) continue;
                    string name = t.Name + "." + m.Name;
                    if (filter != null && name.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                    try
                    {
                        m.Invoke(null, null);
                        passed++;
                        Console.WriteLine("PASS " + name);
                    }
                    catch (TargetInvocationException e)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + name + ": " + e.InnerException.Message);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + name + ": " + e.Message);
                    }
                }
            }
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return (failed > 0 || passed + failed == 0) ? 1 : 0;
        }
    }
}
