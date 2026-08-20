using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AudioCore.Models;

public static class Tracer
{
    public static void Trace([CallerFilePath] string path = null!, [CallerMemberName] string method = null!) 
        => Debug.WriteLine($"TRACE: ____________ {Path.GetFileNameWithoutExtension(path)}.{method}() called");
    public static void Trace<T>(T args, [CallerArgumentExpression("args")] string argsExpression = "", [CallerFilePath] string path = null!, [CallerMemberName] string method = "")
        => Debug.WriteLine($"TRACE: ____________ {Path.GetFileNameWithoutExtension(path)}.{method}({argsExpression}: {args}) called");
    public static void Trace<T1, T2>(T1 arg1, T2 arg2, [CallerArgumentExpression("arg1")] string arg1Expression = "", [CallerArgumentExpression("arg2")] string arg2Expression = "", [CallerFilePath] string path = null!, [CallerMemberName] string method = "")
        => Debug.WriteLine($"TRACE: ____________ {Path.GetFileNameWithoutExtension(path)}.{method}({arg1Expression}: {arg1}, {arg2Expression}: {arg2}) called");
    public static void Msg(string message, [CallerFilePath] string path = null!, [CallerMemberName] string method = null!)
        => Debug.WriteLine($"TRACE: ____________ {Path.GetFileNameWithoutExtension(path)}.{method}(): {message}");
}
