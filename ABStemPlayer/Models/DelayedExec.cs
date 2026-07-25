using System;
using System.Collections.Generic;
using System.Text;

namespace ABStemPlayer.Models;

public class DelayedExec
{
    private CancellationTokenSource? _cts;
    private TimeSpan _timeout;

    public DelayedExec(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public Task Exec( Func<CancellationToken, Task> action)
    {
        if ( _cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();
        return Task.Run(() => DoAction(action, _cts.Token), _cts.Token);
    }

    private async Task DoAction(Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            await Task.Delay(_timeout, token).ConfigureAwait(false);
            await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
    }
}
