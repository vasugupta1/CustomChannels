using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

//No capacity
// var unboundedChannel = Channel.CreateUnbounded<int>();
// with 100 capacity, if its bounded then writters will have to wait to write if the capacity has been reached.
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
    private readonly ConcurrentQueue<T> _items = [];
    /// <summary>
    /// A queue of promises so if a reader tries to read when a the channel is empty, it leaves a queues a request,
    /// the next writer will fulfil that request.
    /// Reader arrives: There is no data. The reader creates a new TaskCompletionSource<T>(). It adds this to a queue and returns tcs.Task. The reader is now "suspended" (awaiting).
    //Writer arrives: The writer sees a waiting reader in the queue. The writer takes the tcs and calls tcs.TrySetResult(data).
    //The Link: The moment the writer calls that method, the reader's await finishes, and they receive the data
    /// </summary>
    private readonly ConcurrentQueue<TaskCompletionSource<T>> _readers = [];
    /// <summary>
    /// WaitingReaders will just signal to consumer
    /// </summary>
    private TaskCompletionSource<bool>? _waitingReaders;
    private object SyncObject => _items;
    private bool _completed;

    public ValueTask<T> ReadAsync()
    {
        // double check locking going on here
        // check without lock first and then check with the lock to make sure
        if (_items.TryDequeue(out var item))
        {
            return ValueTask.FromResult(item);
        }
        
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
                //Writer completes the reader’s task → reader wakes up immediately
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
            ///We need to set all of the TCS and throw exception because that promise will not be fulfilled
            /// because its completed
            while (_readers.TryDequeue(out var result))
            {
                result.TrySetException(new InvalidOperationException("Already completed"));
            }

            var waitingReaders = _waitingReaders;
            _waitingReaders = null;
            waitingReaders?.TrySetResult(false);
        }
    }
    
    /// <summary>
    /// Tell me when its possible to call ReadAsync
    /// </summary>
    /// <returns></returns>
    public ValueTask<bool> WaitToReadAsync()
    {
        if (!_items.IsEmpty)
        {
            return ValueTask.FromResult(true);
        }
        
        lock (SyncObject)
        {
            if (!_items.IsEmpty)
            {
                return ValueTask.FromResult(true);
            }
            
            if (_completed)
            {
                return ValueTask.FromResult(false);
            }
        }
        
        //If no data for you to read then wait, by creating a TCS setting that to WaitingReader and return that TCS
        // So when this TCS result has been set the caller of WaitToReadAsync can operate again
        _waitingReaders ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new ValueTask<bool>(_waitingReaders.Task);
    }
    
    public bool TryRead([MaybeNullWhen(false)] out T result)
        => _items.TryDequeue(out result);

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