using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Imazen.Common.Concurrency;
using Imazen.Common.Extensibility.StreamCache;
using Microsoft.Extensions.Logging;

namespace Imazen.HybridCache
{
    internal class AsyncCache
    {
        public enum AsyncCacheDetailResult
        {
            Unknown = 0,
            MemoryHit,
            DiskHit,
            WriteSucceeded,
            QueueLockTimeoutAndCreated,
            FileAlreadyExists,
            Miss,
            CacheEvictionFailed,
            WriteTimedOut,
            QueueLockTimeoutAndFailed,
            EvictAndWriteLockTimedOut,
            ContendedDiskHit
        }
        public class AsyncCacheResult : IStreamCacheResult
        {
            public Stream Data { get; set; }
            public string ContentType { get; set; }
            public string Status => Detail.ToString();
            
            public AsyncCacheDetailResult Detail { get; set; }
        }
        
        public AsyncCache(AsyncCacheOptions options, ICacheCleanupManager cleanupManager,HashBasedPathBuilder pathBuilder, ILogger logger)
        {
            Options = options;
            PathBuilder = pathBuilder;
            CleanupManager = cleanupManager;
            Logger = logger;
            FileWriteLocks = new AsyncLockProvider();
            QueueLocks = new AsyncLockProvider();
            EvictAndWriteLocks = new AsyncLockProvider();
            CurrentWrites = new AsyncWriteCollection(options.MaxQueuedBytes);
            FileWriter = new CacheFileWriter(FileWriteLocks, Options.MoveFileOverwriteFunc, Options.MoveFilesIntoPlace);
        }

        
        private AsyncCacheOptions Options { get; }
        private HashBasedPathBuilder PathBuilder { get; }
        private ILogger Logger { get; }

        private ICacheCleanupManager CleanupManager { get; }
        
        /// <summary>
        /// Provides string-based locking for file write access.
        /// </summary>
        private AsyncLockProvider FileWriteLocks {get; }
        
        
        private AsyncLockProvider EvictAndWriteLocks {get; }
        
        private CacheFileWriter FileWriter { get; }

        /// <summary>
        /// Provides string-based locking for image resizing (not writing, just processing). Prevents duplication of efforts in asynchronous mode, where 'Locks' is not being used.
        /// </summary>
        private AsyncLockProvider QueueLocks { get;  }

        /// <summary>
        /// Contains all the queued and in-progress writes to the cache. 
        /// </summary>
        private AsyncWriteCollection CurrentWrites {get; }


        public Task AwaitEnqueuedTasks()
        {
            return CurrentWrites.AwaitAllCurrentTasks();
        }
        
        private static bool IsFileLocked(IOException exception) {
            //For linux
            const int linuxEAgain = 11;
            const int linuxEBusy = 16;
            const int linuxEPerm = 13;
            if (linuxEAgain == exception.HResult || linuxEBusy == exception.HResult || linuxEPerm == exception.HResult)
            {
                return true;
            }
            //For windows
            // See https://docs.microsoft.com/en-us/dotnet/standard/io/handling-io-errors
            const int errorSharingViolation = 0x20; 
            const int errorLockViolation = 0x21;
            var errorCode = exception.HResult & 0x0000FFFF; 
            return errorCode == errorSharingViolation || errorCode == errorLockViolation;
        }

        private async Task<FileStream> TryWaitForLockedFile(string physicalPath, Stopwatch waitTime, int timeoutMs, CancellationToken cancellationToken)
        {
            var waitForFile = waitTime;
            waitTime.Stop();
            while (waitForFile.ElapsedMilliseconds < timeoutMs)
            {
                waitForFile.Start();
                try
                {
                    var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    waitForFile.Stop();
                    Logger?.LogInformation("Cache file locked, waited {WaitTime} to read {Path}", waitForFile.Elapsed, physicalPath);
                    return fs;
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (IOException iex)
                {
                    if (!IsFileLocked(iex)) throw;
                }
                catch (UnauthorizedAccessException)
                {
                    
                }
                await Task.Delay((int)Math.Min(15, Math.Round(timeoutMs / 3.0)), cancellationToken); 
                waitForFile.Stop();
            }

            return null;
        }

        private async Task<AsyncCacheResult> TryWaitForLockedFile(CacheEntry entry, string contentType, CancellationToken cancellationToken)
        {
            FileStream openedStream = null;
            var waitTime = Stopwatch.StartNew();
            if (!await FileWriteLocks.TryExecuteAsync(entry.StringKey, Options.WaitForIdenticalDiskWritesMs,
                cancellationToken, async () =>
                {
                    openedStream = await TryWaitForLockedFile(entry.PhysicalPath, waitTime, 
                        Options.WaitForIdenticalDiskWritesMs, cancellationToken);
                }))
            {
                return null;
            }

            if (openedStream != null)
            {
                return new AsyncCacheResult
                {
                    Detail = AsyncCacheDetailResult.ContendedDiskHit,
                    ContentType = contentType,
                    Data = openedStream
                };
            }

            return null;
        }

        private async Task<AsyncCacheResult> TryGetFileBasedResult(CacheEntry entry, bool waitForFile, bool retrieveContentType, CancellationToken cancellationToken)
        {
            if (!File.Exists(entry.PhysicalPath)) return null;
            
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            var contentType = retrieveContentType
                ? await CleanupManager.GetContentType(entry, cancellationToken)
                : null;

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            try
            {
                return new AsyncCacheResult
                {
                    Detail = AsyncCacheDetailResult.DiskHit,
                    ContentType = contentType,
                    Data = new FileStream(entry.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan)
                };
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                if (!waitForFile) return null;

                return await TryWaitForLockedFile(entry, contentType, cancellationToken);
            }

            catch (IOException ioException)
            {
                if (!waitForFile) return null;
                
                if (IsFileLocked(ioException))
                {
                    return await TryWaitForLockedFile(entry, contentType, cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }
        
        /// <summary>
        /// Tries to fetch the result from disk cache, the memory queue, or create it. If the memory queue has space,
        /// the writeCallback() will be executed and the resulting bytes put in a queue for writing to disk.
        /// If the memory queue is full, writing to disk will be attempted synchronously.
        /// In either case, writing to disk can also fail if the disk cache is full and eviction fails.
        /// If the memory queue is full, eviction will be done synchronously and can cause other threads to time out
        /// while waiting for QueueLock
        /// </summary>
        /// <param name="key"></param>
        /// <param name="dataProviderCallback"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="retrieveContentType"></param>
        /// <returns></returns>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public async Task<AsyncCacheResult> GetOrCreateBytes(
            byte[] key,
            AsyncBytesResult dataProviderCallback,
            CancellationToken cancellationToken,
            bool retrieveContentType)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            var swGetOrCreateBytes = Stopwatch.StartNew();
            var entry = new CacheEntry(key, PathBuilder);
            
            // Tell cleanup what we're using
            CleanupManager.NotifyUsed(entry);
            
            // Fast path on disk hit
            var swFileExists = Stopwatch.StartNew();

            var fileBasedResult = await TryGetFileBasedResult(entry, false, retrieveContentType, cancellationToken);
            if (fileBasedResult != null)
            {
                return fileBasedResult;
            }
            // Just continue on creating the file. It must have been deleted between the calls
        
            swFileExists.Stop();
            


            var cacheResult = new AsyncCacheResult();
            
            //Looks like a miss. Let's enter a lock for the creation of the file. This is a different locking system
            // than for writing to the file 
            //This prevents two identical requests from duplicating efforts. Different requests don't lock.

            //Lock execution using relativePath as the sync basis. Ignore casing differences. This prevents duplicate entries in the write queue and wasted CPU/RAM usage.
            var queueLockComplete = await QueueLocks.TryExecuteAsync(entry.StringKey,
                Options.WaitForIdenticalRequestsTimeoutMs, cancellationToken,
                async () =>
                {
                    var swInsideQueueLock = Stopwatch.StartNew();
                    
                    // Now, if the item we seek is in the queue, we have a memcached hit.
                    // If not, we should check the filesystem. It's possible the item has been written to disk already.
                    // If both are a miss, we should see if there is enough room in the write queue.
                    // If not, switch to in-thread writing. 

                    var existingQueuedWrite = CurrentWrites.Get(entry.StringKey);

                    if (existingQueuedWrite != null)
                    {
                        cacheResult.Data = existingQueuedWrite.GetReadonlyStream();
                        cacheResult.ContentType = existingQueuedWrite.ContentType;
                        cacheResult.Detail = AsyncCacheDetailResult.MemoryHit;
                        return;
                    }
                    
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    swFileExists.Start();
                    // Fast path on disk hit, now that we're in a synchronized state
                    var fileBasedResult2 = await TryGetFileBasedResult(entry, true, retrieveContentType, cancellationToken);
                    if (fileBasedResult2 != null)
                    {
                        cacheResult = fileBasedResult2;
                        return;
                    }
                    // Just continue on creating the file. It must have been deleted between the calls
                
                    swFileExists.Stop();

                    var swDataCreation = Stopwatch.StartNew();
                    //Read, resize, process, and encode the image. Lots of exceptions thrown here.
                    var result = await dataProviderCallback(cancellationToken);
                    swDataCreation.Stop();
                    
                    //Create AsyncWrite object to enqueue
                    var w = new AsyncWrite(entry.StringKey, result.Bytes, result.ContentType);

                    cacheResult.Detail = AsyncCacheDetailResult.Miss;
                    cacheResult.ContentType = w.ContentType;
                    cacheResult.Data = w.GetReadonlyStream();

                    // Create a lambda which we can call either in a spawned Task (if enqueued successfully), or
                    // in this task, if our buffer is full.
                    async Task<AsyncCacheDetailResult> EvictWriteAndLogUnsynchronized(bool queueFull, TimeSpan dataCreationTime,  CancellationToken ct)
                    {
                        var delegateStartedAt = DateTime.UtcNow;
                        var swReserveSpace = Stopwatch.StartNew();
                        //We only permit eviction proceedings from within the queue or if the queue is disabled
                        var allowEviction = !queueFull || CurrentWrites.MaxQueueBytes <= 0;
                        var reserveSpaceResult = await CleanupManager.TryReserveSpace(entry, w.ContentType, 
                            w.GetUsedBytes(), allowEviction, EvictAndWriteLocks, ct);
                        swReserveSpace.Stop();

                        var syncString = queueFull ? "synchronous" : "async";
                        if (!reserveSpaceResult.Success)
                        {
                            Logger?.LogError(
                                queueFull
                                    ? "HybridCache synchronous eviction failed; {Message}. Time taken: {1}ms - {2}"
                                    : "HybridCache async eviction failed; {Message}. Time taken: {1}ms - {2}",
                                syncString, reserveSpaceResult.Message, swReserveSpace.ElapsedMilliseconds,
                                entry.RelativePath);

                            return AsyncCacheDetailResult.CacheEvictionFailed;
                        }

                        var swIo = Stopwatch.StartNew();
                        // We only force an immediate File.Exists check when running from the Queue
                        // Otherwise it happens inside the lock
                        var fileWriteResult = await FileWriter.TryWriteFile(entry, delegate(Stream s, CancellationToken ct2)
                        {
                            if (ct2.IsCancellationRequested) throw new OperationCanceledException(ct2);

                            var fromStream = w.GetReadonlyStream();
                            return fromStream.CopyToAsync(s, 81920, ct2);
                        }, !queueFull, Options.WaitForIdenticalDiskWritesMs, ct);
                        swIo.Stop();

                        var swMarkCreated = Stopwatch.StartNew();
                        // Mark the file as created so it can be deleted
                        await CleanupManager.MarkFileCreated(entry, 
                            w.ContentType, 
                            w.GetUsedBytes(),
                            DateTime.UtcNow);
                        swMarkCreated.Stop();
                        
                        switch (fileWriteResult)
                        {
                            case CacheFileWriter.FileWriteStatus.LockTimeout:
                                //We failed to lock the file.
                                Logger?.LogWarning("HybridCache {Sync} write failed; disk lock timeout exceeded after {IoTime}ms - {Path}", 
                                    syncString, swIo.ElapsedMilliseconds, entry.RelativePath);
                                return AsyncCacheDetailResult.WriteTimedOut;
                            case CacheFileWriter.FileWriteStatus.FileAlreadyExists:
                                Logger?.LogTrace("HybridCache {Sync} write found file already exists in {IoTime}ms, after a {DelayTime}ms delay and {CreationTime}- {Path}", 
                                    syncString, swIo.ElapsedMilliseconds, 
                                    delegateStartedAt.Subtract(w.JobCreatedAt).TotalMilliseconds, 
                                    dataCreationTime, entry.RelativePath);
                                return AsyncCacheDetailResult.FileAlreadyExists;
                            case CacheFileWriter.FileWriteStatus.FileCreated:
                                if (queueFull)
                                {
                                    Logger?.LogTrace(@"HybridCache synchronous write complete. Create: {CreateTime}ms. Write {WriteTime}ms. Mark Created: {MarkCreatedTime}ms. Eviction: {EvictionTime}ms - {Path}", 
                                        Math.Round(dataCreationTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture).PadLeft(4),
                                        swIo.ElapsedMilliseconds.ToString().PadLeft(4),
                                        swMarkCreated.ElapsedMilliseconds.ToString().PadLeft(4), 
                                        swReserveSpace.ElapsedMilliseconds.ToString().PadLeft(4), entry.RelativePath);
                                }
                                else
                                {
                                    Logger?.LogTrace(@"HybridCache async write complete. Create: {CreateTime}ms. Write {WriteTime}ms. Mark Created: {MarkCreatedTime}ms Eviction {EvictionTime}ms. Delay {DelayTime}ms. - {Path}", 
                                        Math.Round(dataCreationTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture).PadLeft(4),
                                        swIo.ElapsedMilliseconds.ToString().PadLeft(4), 
                                        swMarkCreated.ElapsedMilliseconds.ToString().PadLeft(4), 
                                        swReserveSpace.ElapsedMilliseconds.ToString().PadLeft(4), 
                                        Math.Round(delegateStartedAt.Subtract(w.JobCreatedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture).PadLeft(4), 
                                        entry.RelativePath);
                                }

                                return AsyncCacheDetailResult.WriteSucceeded;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }

                    async Task<AsyncCacheDetailResult> EvictWriteAndLogSynchronized(bool queueFull,
                        TimeSpan dataCreationTime, CancellationToken ct)
                    {
                        var cacheDetailResult = AsyncCacheDetailResult.Unknown;
                        var writeLockComplete = await EvictAndWriteLocks.TryExecuteAsync(entry.StringKey,
                            Options.WaitForIdenticalRequestsTimeoutMs, cancellationToken,
                            async () =>
                            {
                                cacheDetailResult =
                                    await EvictWriteAndLogUnsynchronized(queueFull, dataCreationTime, ct);
                            });
                        if (!writeLockComplete)
                        {
                            cacheDetailResult = AsyncCacheDetailResult.EvictAndWriteLockTimedOut;
                        }

                        return cacheDetailResult;
                    }
                    

                    var swEnqueue = Stopwatch.StartNew();
                    var queueResult = CurrentWrites.Queue(w, async delegate
                    {
                        try
                        {
                            var unused = await EvictWriteAndLogSynchronized(false, swDataCreation.Elapsed, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError(ex, "HybridCache failed to flush async write, {Exception} {Path}\n{StackTrace}", ex.ToString(),
                                entry.RelativePath, ex.StackTrace);
                        }

                    });
                    swEnqueue.Stop();
                    swInsideQueueLock.Stop();
                    swGetOrCreateBytes.Stop();

                    if (queueResult == AsyncWriteCollection.AsyncQueueResult.QueueFull)
                    {
                        if (Options.WriteSynchronouslyWhenQueueFull)
                        {
                            var writerDelegateResult = await EvictWriteAndLogSynchronized(true, swDataCreation.Elapsed, cancellationToken);
                            cacheResult.Detail = writerDelegateResult;
                        }
                    }
                });
            if (!queueLockComplete)
            {
                //On queue lock failure
                if (!Options.FailRequestsOnEnqueueLockTimeout)
                {
                    // We run the callback with no intent of caching
                    var cacheInputEntry = await dataProviderCallback(cancellationToken);
                    
                    cacheResult.Detail = AsyncCacheDetailResult.QueueLockTimeoutAndCreated;
                    cacheResult.ContentType = cacheInputEntry.ContentType;
                    cacheResult.Data = new MemoryStream(cacheInputEntry.Bytes.Array ?? throw new NullReferenceException(), 
                        cacheInputEntry.Bytes.Offset, cacheInputEntry.Bytes.Count, false, true);
                }
                else
                {
                    cacheResult.Detail = AsyncCacheDetailResult.QueueLockTimeoutAndFailed;
                }
            }
            return cacheResult;
        }
    }
}