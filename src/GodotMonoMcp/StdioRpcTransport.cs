using System.Text;

namespace GodotMonoMcp;

internal enum RpcWireMode
{
    Unknown = 0,
    Line = 1,
    Framed = 2
}

internal sealed class StdioRpcTransport
{
    private static readonly byte[] FramedBoundary = "\r\n\r\n"u8.ToArray();
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private RpcWireMode _mode = RpcWireMode.Unknown;

    public StdioRpcTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task<string?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        return _mode switch
        {
            RpcWireMode.Line => await ReadLineMessageAsync(cancellationToken),
            RpcWireMode.Framed => await ReadFramedMessageAsync(cancellationToken),
            _ => await ReadFirstMessageAndDetectModeAsync(cancellationToken)
        };
    }

    public async Task WriteMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_mode == RpcWireMode.Line)
        {
            var lineBytes = Encoding.UTF8.GetBytes(message + "\n");
            await _output.WriteAsync(lineBytes, cancellationToken);
            await _output.FlushAsync(cancellationToken);
            return;
        }

        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken);
        await _output.WriteAsync(body, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    private async Task<string?> ReadFirstMessageAndDetectModeAsync(CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(_input, cancellationToken);
        if (first < 0)
        {
            return null;
        }

        while (first is '\r' or '\n')
        {
            first = await ReadByteAsync(_input, cancellationToken);
            if (first < 0)
            {
                return null;
            }
        }

        if (first == '{' || first == '[')
        {
            _mode = RpcWireMode.Line;
            return await ReadLineFromFirstByteAsync((byte)first, cancellationToken);
        }

        _mode = RpcWireMode.Framed;
        return await ReadFramedFromFirstByteAsync((byte)first, cancellationToken);
    }

    private async Task<string?> ReadLineMessageAsync(CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(_input, cancellationToken);
        if (first < 0)
        {
            return null;
        }

        while (first is '\r' or '\n')
        {
            first = await ReadByteAsync(_input, cancellationToken);
            if (first < 0)
            {
                return null;
            }
        }

        return await ReadLineFromFirstByteAsync((byte)first, cancellationToken);
    }

    private async Task<string?> ReadLineFromFirstByteAsync(byte firstByte, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256) { firstByte };
        while (true)
        {
            var next = await ReadByteAsync(_input, cancellationToken);
            if (next < 0)
            {
                break;
            }

            if (next == '\n')
            {
                break;
            }

            if (next == '\r')
            {
                continue;
            }

            bytes.Add((byte)next);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task<string?> ReadFramedMessageAsync(CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(_input, cancellationToken);
        if (first < 0)
        {
            return null;
        }

        while (first is '\r' or '\n')
        {
            first = await ReadByteAsync(_input, cancellationToken);
            if (first < 0)
            {
                return null;
            }
        }

        return await ReadFramedFromFirstByteAsync((byte)first, cancellationToken);
    }

    private async Task<string?> ReadFramedFromFirstByteAsync(byte firstByte, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(256) { firstByte };
        while (true)
        {
            if (headerBytes.Count > MaxHeaderBytes)
            {
                throw new InvalidOperationException("MCP header too large");
            }

            var next = await ReadByteAsync(_input, cancellationToken);
            if (next < 0)
            {
                return null;
            }

            headerBytes.Add((byte)next);
            if (EndsWithBoundary(headerBytes))
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = ParseContentLength(headerText);
        var body = new byte[contentLength];
        await ReadExactAsync(_input, body, cancellationToken);
        return Encoding.UTF8.GetString(body);
    }

    private static bool EndsWithBoundary(List<byte> data)
    {
        if (data.Count < FramedBoundary.Length)
        {
            return false;
        }

        var offset = data.Count - FramedBoundary.Length;
        for (var i = 0; i < FramedBoundary.Length; i++)
        {
            if (data[offset + i] != FramedBoundary[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int ParseContentLength(string headerText)
    {
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = line["Content-Length:".Length..].Trim();
            if (int.TryParse(raw, out var value) && value >= 0)
            {
                return value;
            }
        }

        throw new InvalidOperationException("Missing or invalid Content-Length header");
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading MCP message body");
            }

            read += count;
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        var count = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
        return count == 0 ? -1 : one[0];
    }
}
