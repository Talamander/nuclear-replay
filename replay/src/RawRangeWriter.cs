using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using Unity.Collections;

namespace NuclearReplay.Playback
{
    // Appends raw RGBA frames to one file (or an encoder's stdin) on a background thread. Buffers are
    // pooled so the main thread never allocates a frame-sized array, and the bounded queue applies
    // back-pressure if the disk or the encoder falls behind.
    internal sealed class RawRangeWriter
    {
        private readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(48);
        private readonly ConcurrentBag<byte[]> pool = new ConcurrentBag<byte[]>();
        private readonly Thread thread;
        private readonly string path;
        private readonly Func<RawRangeWriter, Stream> open;
        private readonly ManualLogSource log;
        private volatile bool failed;
        public int Width, Height, Pending, Errors, Frames;
        public bool Failed => failed;

        // The file may be a FIFO that an encoder reads; opening it waits for the reader, so the
        // writer thread opens it and the game thread never blocks on that.
        public RawRangeWriter(string path, ManualLogSource log)
        {
            this.path = path;
            this.log = log;
            thread = new Thread(Run) { IsBackground = true, Name = "nc-raw-writer" };
            thread.Start();
        }

        // Opens the target only when the first frame arrives, so Width and Height are known
        // (render jobs start the encoder process here). label names the target in log messages.
        public RawRangeWriter(Func<RawRangeWriter, Stream> open, string label, ManualLogSource log)
        {
            this.open = open;
            path = label;
            this.log = log;
            thread = new Thread(Run) { IsBackground = true, Name = "nc-raw-writer" };
            thread.Start();
        }

        public void Add(NativeArray<byte> data)
        {
            // A readback that completes after Close (a canceled clip) has nowhere to go.
            if (failed || queue.IsAddingCompleted) { Errors++; return; }
            if (!pool.TryTake(out var buffer) || buffer.Length != data.Length) buffer = new byte[data.Length];
            data.CopyTo(buffer);
            bool added;
            try { added = queue.TryAdd(buffer, 60000); }
            catch (InvalidOperationException) { Errors++; return; }   // closed while waiting for room
            if (!added)
            {
                failed = true; Errors++;
                log.LogError("Raw frame writer stalled for 60 s (no reader on " + path + "?); dropping this range's frames");
            }
        }

        private void Run()
        {
            Stream stream = null;
            try
            {
                if (open == null) stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 22);
                foreach (var buffer in queue.GetConsumingEnumerable())
                {
                    if (stream == null) stream = open(this);
                    stream.Write(buffer, 0, buffer.Length);
                    Frames++;
                    pool.Add(buffer);
                }
            }
            catch (Exception e)
            {
                failed = true; Errors++;
                log.LogError("Raw frame writer failed: " + e);
                foreach (var _ in queue.GetConsumingEnumerable()) { }   // keep draining so Add never blocks
            }
            finally
            {
                try { stream?.Dispose(); }
                catch (Exception e) { failed = true; Errors++; log.LogError("Raw frame writer close failed: " + e.Message); }
            }
        }

        public void Close()
        {
            queue.CompleteAdding();
            if (thread.Join(120000)) return;
            log.LogError("Raw frame writer did not finish within 120 s: " + path);
            if (open != null) return;
            // A writer stuck opening a FIFO nobody reads would hang the game at exit; become the reader
            // for a moment so its open returns, then its writes fail and the thread ends.
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } } catch (Exception) { }
            thread.Join(10000);
        }
    }
}
