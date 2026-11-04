using System.Reflection;

// Invokes ModFramework.Runtime.Loader.Start() the same way Bootstrap does:
// reflection on the runtime assembly, no compile-time reference to the loader type.
var dir = AppContext.BaseDirectory;
var dll = Path.Combine(dir, "ModFramework.Runtime.dll");
if (!File.Exists(dll))
{
    Console.WriteLine("verdict=MISSING-RUNTIME " + dll);
    return 2;
}

var asm = Assembly.LoadFrom(dll);
var type = asm.GetType("ModFramework.Runtime.Loader");
if (type is null)
{
    Console.WriteLine("verdict=MISSING-TYPE");
    return 2;
}

var start = type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
if (start is null)
{
    Console.WriteLine("verdict=MISSING-METHOD");
    return 2;
}

var returned = start.Invoke(null, null);
Console.WriteLine("return-null=" + (returned is null ? "true" : "false"));
Console.WriteLine("return-value=" + (returned?.ToString() ?? "(null)"));

var logPath = Path.Combine(dir, "loader.log");
var reportPath = Path.Combine(dir, "loader-report.txt");
Console.WriteLine("loader-log=" + File.Exists(logPath));
Console.WriteLine("loader-report=" + File.Exists(reportPath));
if (File.Exists(reportPath))
{
    var body = File.ReadAllText(reportPath).Replace("\r", "").Replace("\n", " | ");
    Console.WriteLine("report-body=" + body);
}

return 0;
