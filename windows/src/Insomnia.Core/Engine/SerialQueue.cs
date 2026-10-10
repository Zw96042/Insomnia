namespace Insomnia.Core.Engine;

/// <summary>
/// Runs work one item at a time, in arrival order, on the thread pool. Every
/// engine transaction goes through one queue, so no two of them interleave
/// and none runs on the UI thread.
/// </summary>
public sealed class SerialQueue : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<T> Run<T>(Func<Task<T>> work) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });

    public Task Run(Func<Task> work) =>
        Run(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        });

    public void Dispose() => _gate.Dispose();
}
