namespace PaintDotNet.Mcp.Protocol;

public sealed class BridgeMailbox
{
    private readonly object gate = new();
    private PrepareInspectRequest? inspect;
    private TaskCompletionSource<InspectResponse>? inspectWait;
    private StageBatchRequest? batch;
    private TaskCompletionSource<BatchResponse>? batchWait;

    public PrepareInspectRequest? PeekInspect()
    {
        lock (gate)
        {
            return inspect;
        }
    }

    public StageBatchRequest? PeekBatch()
    {
        lock (gate)
        {
            return batch;
        }
    }

    public InspectResponse WaitForInspect(PrepareInspectRequest request, TimeSpan timeout)
    {
        TaskCompletionSource<InspectResponse> wait;
        lock (gate)
        {
            inspect = request;
            inspectWait = wait = NewWait<InspectResponse>();
        }

        return Wait(wait, timeout, () =>
        {
            lock (gate)
            {
                if (ReferenceEquals(inspectWait, wait))
                {
                    inspect = null;
                    inspectWait = null;
                }
            }
        }, new InspectResponse(request.RequestId, false, "timeout", null));
    }

    public BatchResponse WaitForBatch(StageBatchRequest request, TimeSpan timeout)
    {
        TaskCompletionSource<BatchResponse> wait;
        lock (gate)
        {
            batch = request;
            batchWait = wait = NewWait<BatchResponse>();
        }

        return Wait(wait, timeout, () =>
        {
            lock (gate)
            {
                if (ReferenceEquals(batchWait, wait))
                {
                    batch = null;
                    batchWait = null;
                }
            }
        }, new BatchResponse(request.BatchId, false, "timeout", null, null, null));
    }

    public void CompleteInspect(InspectResponse response)
    {
        TaskCompletionSource<InspectResponse>? wait;
        lock (gate)
        {
            wait = inspectWait;
            inspect = null;
            inspectWait = null;
        }

        wait?.TrySetResult(response);
    }

    public void CompleteBatch(BatchResponse response)
    {
        TaskCompletionSource<BatchResponse>? wait;
        lock (gate)
        {
            wait = batchWait;
            batch = null;
            batchWait = null;
        }

        wait?.TrySetResult(response);
    }

    private static TaskCompletionSource<T> NewWait<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Wait<T>(TaskCompletionSource<T> wait, TimeSpan timeout, Action clear, T timedOut)
    {
        try
        {
            if (!wait.Task.Wait(timeout))
            {
                clear();
                wait.TrySetResult(timedOut);
                return timedOut;
            }

            return wait.Task.Result;
        }
        finally
        {
            clear();
        }
    }
}
