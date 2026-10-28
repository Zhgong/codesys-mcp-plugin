using System;
using System.Collections.Generic;
using System.Reflection;

namespace CodesysMcpTests
{
    public static class Assert
    {
        public static void AreEqual<T>(T expected, T actual, string message = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"Assert.AreEqual failed. Expected: <{expected}>, Actual: <{actual}>. {message}");
            }
        }

        public static void IsTrue(bool condition, string message = "")
        {
            if (!condition)
            {
                throw new Exception($"Assert.IsTrue failed. {message}");
            }
        }

        public static void IsFalse(bool condition, string message = "")
        {
            if (condition)
            {
                throw new Exception($"Assert.IsFalse failed. {message}");
            }
        }

        public static void IsNotNull(object obj, string message = "")
        {
            if (obj == null)
            {
                throw new Exception($"Assert.IsNotNull failed. {message}");
            }
        }

        public static void IsNull(object obj, string message = "")
        {
            if (obj != null)
            {
                throw new Exception($"Assert.IsNull failed. {message}");
            }
        }

        public static void Contains(string expectedSubstring, string actualString, string message = "")
        {
            if (actualString == null || !actualString.Contains(expectedSubstring))
            {
                throw new Exception($"Assert.Contains failed. Expected substring: <{expectedSubstring}> in <{actualString}>. {message}");
            }
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class TestMethodAttribute : Attribute { }

    class Program
    {
        static int Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================");
            Console.WriteLine(" Running CodesysMcp C# Core Unit Tests");
            Console.WriteLine("==========================================================");
            Console.ResetColor();

            var testClasses = new Type[]
            {
                typeof(ProtocolTests),
                typeof(ToolValidationTests),
                typeof(StreamFramingTests),
                typeof(SearchEngineTests)
            };

            int passed = 0;
            int failed = 0;

            foreach (var testClass in testClasses)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[{testClass.Name}]");
                Console.ResetColor();

                object instance = Activator.CreateInstance(testClass);
                var methods = testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance);

                foreach (var method in methods)
                {
                    if (method.GetCustomAttribute<TestMethodAttribute>() == null) continue;

                    try
                    {
                        method.Invoke(instance, null);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [PASS] {method.Name}");
                        Console.ResetColor();
                        passed++;
                    }
                    catch (TargetInvocationException ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [FAIL] {method.Name}: {ex.InnerException?.Message ?? ex.Message}");
                        Console.ResetColor();
                        failed++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [FAIL] {method.Name}: {ex.Message}");
                        Console.ResetColor();
                        failed++;
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------------");
            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Result: ALL {passed} TESTS PASSED!");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Result: {failed} FAILED, {passed} PASSED.");
                Console.ResetColor();
                return 1;
            }
        }
    }
}
