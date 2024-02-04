using k8s;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace immersive.kubernetesproxy.services;
public sealed class TerminalService
{
    // indexed by "podName/namespaceName/containerName"
    private ConcurrentDictionary<string, TerminalSession> sessions = new();
    private Kubernetes client;

    private TerminalService(Kubernetes client)
    {
        this.client = client;
    }

    public static async Task<TerminalService> CreateAsync(string configPath)
    {
        KubernetesClientConfiguration config = await BuildConfigFromFile(configPath);
        Kubernetes client = new(config);
        return new TerminalService(client);
    }

    public async Task<string> Exec(string namespaceName, string podName, string? containerName, string command)
    {
        string result;
        TerminalSession session = sessions.GetOrAdd(
        $"{namespaceName}/{podName}/{containerName}",
        (_) =>
        {
            TerminalSession session = new(client, namespaceName, podName, containerName);
            CancellationTokenSource timeout = new(2000);
            try
            {
                session.Connect(timeout.Token).Wait();
            }
            catch ( Exception e )
            {
                Console.WriteLine("Failed to connect" + e.GetType() + ":" + e.Message);
            }
            return session;
        });
        if ( session.IsConnected )
        {
            result = await session.Exec(command);
        }
        else
        {
            result = $"Failed to connect to {namespaceName}/{podName}/{containerName}";
        }
        return result;
    }

    private static async Task<KubernetesClientConfiguration> BuildConfigFromFile(string configPath)
    {
        string configText = File.ReadAllText(configPath);
        Stream memStream = new MemoryStream(Encoding.UTF8.GetBytes(configText));
        KubernetesClientConfiguration config1 = await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(memStream);
        return config1;
    }

    private sealed class TerminalSession(Kubernetes client, string namespaceName, string podName, string? containerName) : IDisposable
    {
        private const int DEFAULT_CONNECT_TIMEOUT_MS = 2000;
        private const int READ_TIMEOUT_MS = 300;
        private const int POLL_PERIOD_MS = 100;
        private const string KILL_COMMAND = "exit";
        private const string COMMAND_TERMINATOR = "\r";

        private readonly Kubernetes client = client;
        private readonly string podName = podName;
        private readonly string namespaceName = namespaceName;
        private readonly string? containerName = containerName;

        private readonly Channel<string> commands = Channel.CreateUnbounded<string>();
        private readonly Channel<string> outputs = Channel.CreateUnbounded<string>();
        private readonly Channel<string> errors = Channel.CreateUnbounded<string>();

        public bool IsConnected { get; private set; }

        public async Task Connect(CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"Connect");

            List<string> command =
            [
                "/bin/bash",
                "-i" // interactive
            ];
            var result = Task.Run(async () => await client.NamespacedPodExecAsync(podName, namespaceName, containerName, command, true, HandleStreams, cancellationToken: cancellationToken));
            SpinWait.SpinUntil(() => IsConnected, DEFAULT_CONNECT_TIMEOUT_MS);
            string promptTask = await outputs.Reader.ReadAsync(cancellationToken).AsTask();
            Console.WriteLine($"Consume initial prompt: " + promptTask);
            Console.WriteLine("==================================================================================");
        }

        public async Task<string> Exec(string command, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"Exec: {command}");

            string output = "";
            if ( commands.Writer.TryWrite(command) )
            {
                CancellationTokenSource readCancellation = new(READ_TIMEOUT_MS);
                while ( !cancellationToken.IsCancellationRequested && !readCancellation.IsCancellationRequested )
                {
                    try
                    {
                        readCancellation = new(READ_TIMEOUT_MS);
                        output += await outputs.Reader.ReadAsync(readCancellation.Token);
                    }
                    catch ( Exception e )
                    {
                        Console.WriteLine("Nothing more to read: " + e.Message);
                    }
                }
            }
            else
            {
                output = $"Failed to write command {command} to {namespaceName}/{podName}/{containerName}";
            }
            Console.WriteLine("Exec ends.");
            Console.WriteLine("=======================================");
            Console.WriteLine(output);
            Console.WriteLine("=======================================");
            return StripControlChars(StripExtended(output));
        }

        static string StripControlChars(string arg)
        {
            char[] arrForm = arg.ToCharArray();
            StringBuilder buffer = new(arg.Length);//This many chars at most

            foreach ( char ch in arrForm )
                if ( ch == '\r' || ch == '\n' || !Char.IsControl(ch) ) buffer.Append(ch);//Only add to buffer if not a control char (keep CR LF)

            return buffer.ToString();
        }

        static string StripExtended(string arg)
        {
            StringBuilder buffer = new(arg.Length); //Max length
            foreach ( char ch in arg )
            {
                UInt16 num = Convert.ToUInt16(ch);//In .NET, chars are UTF-16
                //The basic characters have the same code points as ASCII, and the extended characters are bigger
                if ( ((num >= 32u) && (num <= 126u)) || ch == '\r' || ch == '\n' ) buffer.Append(ch); //(keep CR LF)
            }
            return buffer.ToString();
        }

        private Task HandleStreams(Stream stdIn, Stream stdOut, Stream stdErr)
        {
            Task stdInTask = Task.Run(async () =>
            {
                StreamWriter stdInWriter = new(stdIn)
                {
                    AutoFlush = true
                };
                await foreach ( string command in commands.Reader.ReadAllAsync() )
                {
                    await stdInWriter.WriteAsync(command + COMMAND_TERMINATOR);
                }
            });

            Task stdOutTask = Task.Run(async () =>
            {
                StreamReader stdOutReader = new(stdOut);
                char[] buffer = new char[10000];
                while ( !stdOutReader.EndOfStream )
                {
                    int read = await stdOutReader.ReadAsync(buffer);
                    Array.Clear(buffer, read, buffer.Length - read);
                    string output = new(buffer);
                    //Console.WriteLine($"({read} at time {DateTime.Now}) output:\n {output}");
                    outputs.Writer.TryWrite(output);
                    await Task.Delay(POLL_PERIOD_MS);
                }
            });
            Task stdErrTask = Task.Run(async () =>
            {
                StreamReader stdErrReader = new(stdErr);
                char[] buffer = new char[1024];
                while ( !stdErrReader.EndOfStream )
                {
                    int read = await stdErrReader.ReadAsync(buffer);
                    Array.Clear(buffer, read, buffer.Length - read);
                    string error = new(buffer);
                    //Console.WriteLine($"err:\n {error}");
                    errors.Writer.TryWrite(error);
                    await Task.Delay(POLL_PERIOD_MS);
                }
            });
            IsConnected = true;
            return Task.WhenAll(stdInTask, stdOutTask, stdErrTask);
        }

        public void Dispose()
        {
            commands.Writer.TryWrite(KILL_COMMAND + COMMAND_TERMINATOR);
            commands.Writer.Complete();
            outputs.Writer.Complete();
            errors.Writer.Complete();
        }
    }
}