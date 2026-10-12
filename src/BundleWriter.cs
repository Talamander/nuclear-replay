using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NuclearReplay
{
    // Only immutable strings cross the game-thread boundary.
    public sealed class BundleWriter : IDisposable
    {
        private readonly BlockingCollection<(string channel, string json)> queue;
        private readonly Thread thread;
        private readonly string directory;
        private long droppedEvents, droppedStates, accepted, written;
        private long queuedUtf16Bytes;
        private readonly long byteCapacity;
        private volatile string fault;
        public long DroppedEvents => Interlocked.Read(ref droppedEvents);
        public long DroppedStates => Interlocked.Read(ref droppedStates);
        public string Fault => fault;

        public BundleWriter(string directory, int capacity, long byteCapacity = 32 * 1024 * 1024)
        {
            this.directory = directory;
            this.byteCapacity = Math.Max(1024, byteCapacity);
            queue = new BlockingCollection<(string, string)>(Math.Max(16, capacity));
            thread = new Thread(Run) { IsBackground = true, Name = "NuclearReplay writer" };
            thread.Start();
        }

        public bool Enqueue(string channel, string json)
        {
            // Large motion batches need a payload bound as well as a record-count bound.
            // Reserve before publication so the consumer can safely release the reservation.
            long bytes = (long)json.Length * 2;
            bool added = false;
            if (Interlocked.Add(ref queuedUtf16Bytes, bytes) <= byteCapacity)
            {
                try { added = fault == null && !queue.IsAddingCompleted && queue.TryAdd((channel, json)); }
                catch (InvalidOperationException) { } // Shutdown can close the queue before publication.
            }
            if (added)
            {
                Interlocked.Increment(ref accepted);
                return true;
            }
            Interlocked.Add(ref queuedUtf16Bytes, -bytes);
            if (channel == "states") Interlocked.Increment(ref droppedStates);
            else Interlocked.Increment(ref droppedEvents);
            return false;
        }

        internal void RequireEnqueue(string channel, string json)
        {
            if (!Enqueue(channel, json))
                throw new IOException("Record was not accepted by the bundle writer.");
        }

        private void Run()
        {
            try
            {
                Directory.CreateDirectory(directory);
                using var events = new StreamWriter(Path.Combine(directory, "events.jsonl"), false, new UTF8Encoding(false));
                using var states = new StreamWriter(Path.Combine(directory, "states.jsonl"), false, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "timeline.md"), "# Nuclear Replay\n\nVideo time is unknown until verified alignment. Run tools/context.py export.\n");
                var flush = Stopwatch.StartNew();
                while (!queue.IsCompleted)
                {
                    if (queue.TryTake(out var item, 100))
                    {
                        Interlocked.Add(ref queuedUtf16Bytes, -(long)item.json.Length * 2);
                        if (item.channel == "session") AtomicWrite("session.json", item.json);
                        else (item.channel == "states" ? states : events).WriteLine(item.json);
                        Interlocked.Increment(ref written);
                    }
                    if (flush.Elapsed.TotalSeconds >= 1)
                    {
                        events.Flush(); states.Flush(); WriteStats(false); flush.Restart();
                    }
                }
                events.Flush(); states.Flush(); WriteStats(true);
            }
            catch (Exception e)
            {
                fault = e.GetType().Name + ": " + e.Message;
                try { WriteStats(false); } catch { }
            }
        }

        private void AtomicWrite(string name, string text)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }

        private void WriteStats(bool clean)
        {
            AtomicWrite("writer-status.json", Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                dropped_events = DroppedEvents, dropped_states = DroppedStates,
                accepted_records = Interlocked.Read(ref accepted), written_records = Interlocked.Read(ref written),
                queued_records = queue.Count, queued_utf16_bytes = Interlocked.Read(ref queuedUtf16Bytes),
                queue_payload_capacity_bytes = byteCapacity, clean_shutdown = clean, fault
            }));
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            // Do not freeze a closing game indefinitely on a failed disk.
            thread.Join(3000);
        }
    }
}
