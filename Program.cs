using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

//No capacity
// var unboundedChannel = Channel.CreateUnbounded<int>();
// with 100 capacity
// var boundedChannel = Channel.CreateBounded<int>(100);

var unboundedChannel = new MyUnboundedChannel<int>();

// _ = Task.Run(async () =>
// {
//     for (var i = 0; i < 100; i++)
//     {
//          await unboundedChannel.Writer.WriteAsync(i);
//          await Task.Delay(1000);
//     }
//
//     
//     unboundedChannel.Writer.Complete();
// });

_ = Task.Run(async () =>
{
    for (var i = 0; i < 10; i++)
    {
        await unboundedChannel.WriteAsync(i);
        await Task.Delay(1000);
    }

    
    unboundedChannel.Complete();
});

// while (true)
// {
//     var result = await unboundedChannel.ReadAsync();
//     Console.WriteLine(result);
// }

await foreach (var item in unboundedChannel.AsAsyncEnumerable())
{
    Console.WriteLine(item);
}
    

while(await unboundedChannel.WaitToReadAsync())
{
    if (unboundedChannel.TryRead(out var result))
    {
        Console.WriteLine(result);
    }
}

class MyUnboundedChannel<T>
{
    private readonly Queue<T> _items = [];
    private readonly Queue<TaskCompletionSource<T>> _readers = [];
    private TaskCompletionSource<bool>? _waitingReaders;
    private object SyncObject => _items;
    private bool _completed;

    public ValueTask<T> ReadAsync()
    {
        lock (SyncObject)
        {
            if (_items.TryDequeue(out var result))
            {
                return ValueTask.FromResult(result);
            }
            
            if (_completed)
            {
                return ValueTask.FromException<T>(new InvalidOperationException("Already completed"));
            }
            
            //TaskCompletionSource is a way of creating Task that I can later complete
            // I can set the Result, Exception or Canceled manually
            // The idea here is that if a reader arrives too early then we create a TaskCompletionSource and waits on its Task
            //A writer that arrives later completes that task
            TaskCompletionSource<T> taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _readers.Enqueue(taskCompletionSource);
            return new ValueTask<T>(taskCompletionSource.Task);
        }
    }
    
    public ValueTask<bool> WaitToReadAsync()
    {
        lock (SyncObject)
        {
            if (_items.Count > 0)
            {
                return ValueTask.FromResult(true);
            }
            
            if (_completed)
            {
                return ValueTask.FromResult(false);
            }
        }
        
        _waitingReaders ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new  ValueTask<bool>(_waitingReaders.Task);
    }

    public ValueTask WriteAsync(T value)
    {
        lock (SyncObject)
        {
            if (_completed)
            {
                return ValueTask.FromException(new InvalidOperationException("Already completed"));
            }
            
            //When writer arrives when a reader is waiting
            // the writer will find a waiting reader through the reader
            // This will get that out and set the result of that to the value that is being passed to write
            //The readers await ReadAsync resumes with value
            if (_readers.TryDequeue(out var result))
            {
                result.TrySetResult(value);
            }
            else
            {
                // here no reader is wating hence we can just store the value in then normal queue 
                // where read will just dequeue and return it back to the caller
                _items.Enqueue(value);

                var waitingReaders = _waitingReaders;
                _waitingReaders = null;
                waitingReaders?.TrySetResult(true);
            }
        }
        return default;
    }

    public void Complete()
    {
        lock (SyncObject)
        {
            _completed = true;
            while (_readers.TryDequeue(out var result))
            {
                result.TrySetException(new InvalidOperationException("Already completed"));
            }

            var waitingReaders = _waitingReaders;
            _waitingReaders = null;
            waitingReaders?.TrySetResult(false);
        }
    }

    public bool TryRead([MaybeNullWhen(false)] out T result)
    {
        lock (SyncObject)
        {
            return _items.TryDequeue(out result);
        }
    }

    public async IAsyncEnumerable<T> AsAsyncEnumerable()
    {
        while (await WaitToReadAsync())
        {
            if (TryRead(out var result))
            {
                yield return result;
            }
        }
    }
}