using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PaintDotNet.Mcp.Protocol;

public sealed class BridgeEndpoint : IDisposable
{
    private readonly BridgeMailbox mailbox;
    private readonly Func<string> pdnVersion;
    private readonly TimeSpan operationTimeout;
    private CancellationTokenSource? cancel;
    private Task? loop;
    private NamedPipeServerStream? pipe;

    public BridgeEndpoint(BridgeMailbox mailbox, Func<string> pdnVersion, TimeSpan? operationTimeout = null)
    {
        this.mailbox = mailbox;
        this.pdnVersion = pdnVersion;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(45);
    }

    public BridgeMailbox Mailbox => mailbox;

    public string? PipeName { get; private set; }

    public void Start(string pipeName)
    {
        if (loop is not null)
        {
            throw new InvalidOperationException("The bridge endpoint is already started.");
        }

        PipeName = pipeName;
        cancel = new CancellationTokenSource();
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current process has no Windows user, so the bridge pipe cannot be locked down.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            64 * 1024,
            64 * 1024,
            security);
        var token = cancel.Token;
        loop = Task.Run(() => Serve(pipe, token));
    }

    public void Dispose()
    {
        cancel?.Cancel();
        try
        {
            pipe?.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancel?.Dispose();
        pipe = null;
        loop = null;
    }

    private void Serve(NamedPipeServerStream server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                server.WaitForConnection();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            try
            {
                while (!token.IsCancellationRequested && server.IsConnected)
                {
                    var message = FrameCodec.Read(server);
                    var response = Handle(message);
                    FrameCodec.Write(server, response);
                }
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidDataException ex)
            {
                TryWriteError(server, "bad_frame", ex.Message);
            }

            try
            {
                if (server.IsConnected)
                {
                    server.Disconnect();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private BridgeMessage Handle(BridgeMessage message)
    {
        switch (message)
        {
            case HandshakeRequest request:
                if (request.Protocol != ProtocolConstants.Version)
                {
                    return new ErrorResponse(
                        "protocol_mismatch",
                        $"Plugin protocol is {ProtocolConstants.Version}. The client sent {request.Protocol}. Reinstall the plugin.");
                }

                return new HandshakeResponse(ProtocolConstants.Version, pdnVersion(), ProtocolConstants.PluginVersion);
            case PrepareInspectRequest request:
                return mailbox.WaitForInspect(request, operationTimeout);
            case StageBatchRequest request:
                return mailbox.WaitForBatch(request, operationTimeout);
            default:
                return new ErrorResponse("bad_request", $"Unexpected message {message.GetType().Name}.");
        }
    }

    private static void TryWriteError(NamedPipeServerStream server, string code, string message)
    {
        try
        {
            if (server.IsConnected)
            {
                FrameCodec.Write(server, new ErrorResponse(code, message));
            }
        }
        catch (IOException)
        {
        }
    }
}
