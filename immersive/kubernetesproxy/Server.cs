using GenHTTP.Engine;
using GenHTTP.Modules.Functional;
using GenHTTP.Modules.IO;
using GenHTTP.Modules.Practices;
using immersive.kubernetesproxy.services;
using System.Net;

if ( args.Length < 1 )
{
    Console.WriteLine($"Please provide path to kube config file");
    return 1;
}
string configPath = args[0];

TerminalService terminalService = await TerminalService.CreateAsync(configPath);

var handler = Inline.Create().Get("/hello", (string name) => $"Hello {name}");


var exec = Inline.Create().Post("/exec", (ExecRequest req) =>
{
    Console.WriteLine($"Executing command {req.command} in {req.namespaceName}/{req.podName}/{req.containerName}");
    Task<string> commandTask = terminalService.Exec(req.namespaceName, req.podName, req.containerName, req.command);
    commandTask.Wait();
    return Content.From(Resource.FromString(commandTask.Result));
});


string ip = "192.168.1.200";
ushort port = 9999;
Console.WriteLine($"Starting Kubernetes Immersive Proxy http://{ip}:{port}");
Console.WriteLine("Press Ctrl+C to exit...");
return Host.Create()
           .Bind(IPAddress.Parse(ip), port)
           .Handler(exec)
           .Defaults()
           .Console()
#if DEBUG
           .Development()
#endif
           .Run();

public record class ExecRequest(string namespaceName, string podName, string? containerName, string command)
{
    public ExecRequest() : this(string.Empty, string.Empty, null, string.Empty) { }
}