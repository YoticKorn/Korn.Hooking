﻿using Korn.Hooking;
using System;

static class TestDifferentMethods
{
    static Type T = typeof(TestDifferentMethods);

    public static void Execute()
    {
        //ConsoleWriteLine();
        Console.WriteLine();
        //StaticDiffArgs();
        Console.WriteLine();
        //StaticDiffArgsRet();
        Console.WriteLine();
        StaticMuchArgs();
        Console.WriteLine();

        Console.ReadLine();

        void ConsoleWriteLine()
        {
            var hook = MethodHook.Create((Action<object>)Console.WriteLine).AddEntryEx(T, nameof(hk_ConsoleWriteLine)).Enable();

            Console.WriteLine((object)3);
            Console.WriteLine((object)4L);
            Console.WriteLine((object)"smth");

            hook.Disable();

            Console.WriteLine((object)"Hook disabled");

            hook.Enable();

            Console.WriteLine((object)"Hook enabled");

            hook.Disable();

            Console.WriteLine((object)"Hook finally disabled");
        }

        void StaticDiffArgs()
        {
            var hook = MethodHook.Create((Action<bool, int, string>)DiffArgs).AddEntryEx(T, nameof(hk_DiffArgs)).Enable();

            DiffArgs(true, 10, "ab");
            DiffArgs(false, 10, "ab");
        }

        void StaticDiffArgsRet()
        {
            var hook = MethodHook.Create((Func<bool, int, string, string>)DiffArgsRet).AddEntryEx(T, nameof(hk_DiffArgsRet)).Enable();

            Console.WriteLine(DiffArgsRet(true, 10, "ab"));
            Console.WriteLine(DiffArgsRet(false, 10, "ab"));
        }

        void StaticMuchArgs()
        {
            var hook = MethodHook.Create((Func<bool, int, int, int, int, int, string>)MuchArgs).AddEntryEx(T, nameof(hk_MuchArgs)).Enable();

            Console.WriteLine($"{hook.DEBUG_Stub.DEBUG_StubRoutine.Address:X}");
            Console.ReadLine();

            Console.WriteLine(MuchArgs(true, 0, 10, 100, 1000, 10000));
            Console.WriteLine(MuchArgs(false, 0, 10, 100, 1000, 10000));
        }
    }

    static bool hk_ConsoleWriteLine(ref object obj)
    {
        if (obj is int)
        {
            obj = $"INTEGER: {obj}";
        }
        else if (obj is long)
        {
            Console.WriteLine($"CS CWL LONG: {obj}");
            return false;
        }
        else
        {
            obj = $"str: {obj}";
        }

        return true;
    }

    static void DiffArgs(bool a, int b, string c)
    {
        Console.WriteLine($"og: {a} {b} {c}");
    }

    static bool hk_DiffArgs(ref bool a, ref int b, ref string c)
    {
        Console.WriteLine($"hk: {a} {b} {c}");
        b += 1;
        c += "z";
        return a;
    }

    static string DiffArgsRet(bool a, int b, string c)
    {
        var str = $"{a} {b} {c}";
        Console.WriteLine("og:" + str);
        return str;
    }

    static bool hk_DiffArgsRet(ref bool a, ref int b, ref string c, ref string result)
    {
        Console.WriteLine($"hk: {a} {b} {c}");
        b += 1;
        c += "z";
        result = "zzz";
        return a;
    }

    static string MuchArgs(bool a, int b, int c, int d, int e, int f)
    {
        var str = $"{a} {b} {c} {d} {e} {f}";
        Console.WriteLine("og:" + str);
        return str;
    }

    static bool hk_MuchArgs(ref bool a, ref int b, ref int c, ref int d, ref int e, ref int f, ref string result)
    {
        b += 1;
        c += 2;
        d += 4;
        e += 8;
        f += 16;
        var str = $"{a} {b} {c} {d} {e} {f}";

        result = str + "z";
        Console.WriteLine("hk:" + str);
        return a;
    }
}