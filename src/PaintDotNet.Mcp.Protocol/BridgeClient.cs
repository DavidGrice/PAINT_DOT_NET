using System.IO.Pipes;

namespace PaintDotNet.Mcp.Protocol;

public sealed class BridgeClient : IDisposable
{
    private readonly NamedPipeClientStream stream;

    private BridgeClient(NamedPipeClientStream stream)
    {
        this.stream = stream;
    }

    public static BridgeClient Connect(string pipeName, TimeSpan timeout)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            stream.Connect((int)timeout.TotalMilliseconds);
            return new BridgeClient(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public HandshakeResponse Handshake(TimeSpan timeout)
    {
        var response = RoundTrip(new HandshakeRequest(ProtocolConstants.Version), () => { }, timeout);
        if (response is ErrorResponse error)
        {
            throw new InvalidOperationException($"{error.Code}: {error.Message}");
        }

        return (HandshakeResponse)response;
    }

    public BridgeMessage RoundTrip(BridgeMessage request, Action trigger, TimeSpan timeout)
    {
        FrameCodec.Write(stream, request);
        using var readDone = new CancellationTokenSource();
        var read = Task.Run(() => FrameCodec.Read(stream), readDone.Token);
        try
        {
            trigger();
        }
        catch
        {
            if (!read.Wait(timeout))
            {
                stream.Dispose();
            }

            throw;
        }

        if (!read.Wait(timeout))
        {
            stream.Dispose();
            throw new TimeoutException("The Paint.NET plugin did not answer before the timeout.");
        }

        return read.Result;
    }

    public void Dispose() => stream.Dispose();
}
