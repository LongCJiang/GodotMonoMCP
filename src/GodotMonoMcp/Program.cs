using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodotMonoMcp;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static async Task Main(string[] args)
    {
        var transportType = "stdio";
        var port = 3000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport" when i + 1 < args.Length:
                    transportType = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var p)) port = p;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return;
            }
        }

        var tools = new GodotTools();
        var server = new McpServer(tools, JsonOptions);

        if (transportType.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            using var sseServer = new SseMcpServer(port, server, JsonOptions);
            await sseServer.RunAsync(cts.Token);
        }
        else
        {
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            var transport = new StdioRpcTransport(input, output);
            await RunStdioAsync(server, transport);
        }
    }

    private static async Task RunStdioAsync(McpServer server, StdioRpcTransport transport)
    {
        while (true)
        {
            var message = await transport.ReadMessageAsync();
            if (message is null)
            {
                break;
            }

            var response = await server.ProcessMessageAsync(message);
            if (response == McpServer.ExitMarker)
            {
                break;
            }

            if (response != null)
            {
                await transport.WriteMessageAsync(response);
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("GodotMonoMCP - Godot Mono 4.6.2 MCP Server");
        Console.WriteLine();
        Console.WriteLine("Usage: GodotMonoMcp [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --transport <stdio|sse>  Transport mode (default: stdio)");
        Console.WriteLine("  --port <number>          Port for SSE transport (default: 3000)");
        Console.WriteLine("  --help, -h               Show this help message");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  GODOT_MONO_PATH          Path to Godot Mono executable");
        Console.WriteLine("  GODOT_PROJECT_PATH       Default Godot project path");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  GodotMonoMcp");
        Console.WriteLine("  GodotMonoMcp --transport sse --port 3000");
    }
}
