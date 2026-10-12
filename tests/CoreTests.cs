using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuclearReplay;

static class CoreTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main()
    {
        var ledger = new Correlation();
        ledger.Add(new Correlation.Launch("session:m1:e10", "launch1", "aircraft1", "darkreach1"));
        ledger.Add(new Correlation.Launch("session:m1:e11", "launch2", "aircraft1", "darkreach2"));
        Check(ledger.Find("session:m1:e10").AssignedTargetId == "darkreach1", "First missile assignment must survive target change");
        Check(ledger.Find("session:m1:e11").AssignedTargetId == "darkreach2", "Second missile assignment must remain independent");
        Check(!ledger.Add(new Correlation.Launch("session:m1:e10", "duplicate", "aircraft2", "other")), "Duplicate launch rejected");
        Check(ledger.Find("missing") == null, "Missing outcome stays uncorrelated");
        ledger.Clear();
        Check(ledger.Find("session:m1:e10") == null, "Mission boundary removes old correlations");
        ledger.Add(new Correlation.Launch("session:m2:e10", "launch3", "respawn", null));
        Check(ledger.Find("session:m2:e10").AssignedTargetId == null, "Unguided target remains unknown");

        // Synthetic client observations must work before (or without) any proven launch.
        Check(ledger.Observe("session:m2:e20", "receipt1", 42), "First client projectile observed");
        Check(ledger.Find("session:m2:e20") == null, "Client observation must not become a launch");
        Check(ledger.Observation("session:m2:e20") == "receipt1", "Observation links survive without launch evidence");
        Check(!ledger.Observe("session:m2:e20", "duplicate", 42), "Repeated lifecycle callbacks deduplicate");
        ledger.Observe("session:m2:e21", "receipt2", 42);
        Check(ledger.Resolve(42) == null, "Ambiguous game IDs must remain uncorrelated");
        ledger.Retire("session:m2:e20", 42);
        Check(ledger.Resolve(42) == null, "Ambiguity persists after removal because delayed effects may refer to either object");
        ledger.Retire("session:m2:e21", 42);
        Check(ledger.Resolve(42) == null, "Removed object cannot receive later damage attribution");
        Check(ledger.Observation("session:m2:e20") == "receipt1", "Historical observation remains after removal");
        ledger.Observe("session:m2:e22", "receipt3", 0);
        Check(ledger.Resolve(0) == null, "Invalid game ID cannot correlate damage");
        ledger.Observe("session:m2:e23", "receipt4", 77);
        Check(ledger.Resolve(77) == "session:m2:e23", "Unique live raw ID can resolve");
        ledger.Retire("session:m2:e23", 77);
        ledger.Observe("session:m2:e24", "receipt5", 77);
        Check(ledger.Resolve(77) == null, "Reused raw ID cannot misattribute a delayed effect");
        ledger.Clear();
        Check(ledger.Observation("session:m2:e20") == null && ledger.Resolve(42) == null, "Mission boundary resets all identity indexes");

        string directory = Path.Combine(Path.GetTempPath(), "nuclear-replay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var writer = new BundleWriter(directory, 16))
            {
                var snapshot = new JObject { ["weapon"] = "Scythe", ["target"] = "Darkreach", ["range_m"] = 33000.125,
                    ["outcome"] = JValue.CreateNull(), ["name"] = "quote\" and newline\n" };
                string frozen = snapshot.ToString(Formatting.None);
                Check(writer.Enqueue("events", frozen), "Initial snapshot accepted");
                snapshot["target"] = "Changed selection";
                for (int i = 0; i < 100000; i++) writer.Enqueue("states", "{\"sample\":" + i + "}");
                Check(writer.DroppedStates > 0, "Bounded queue must report saturation");
            }
            var first = JObject.Parse(File.ReadLines(Path.Combine(directory, "events.jsonl")).First());
            Check((string)first["target"] == "Darkreach", "Writer receives immutable launch snapshot");
            Check((double)first["range_m"] == 33000.125 && first["outcome"].Type == JTokenType.Null, "Precision and unknown preserved");
            Check((string)first["name"] == "quote\" and newline\n", "JSON string escaping survives serialization");
            foreach (string line in File.ReadLines(Path.Combine(directory, "states.jsonl"))) JObject.Parse(line);
            var status = JObject.Parse(File.ReadAllText(Path.Combine(directory, "writer-status.json")));
            Check((bool)status["clean_shutdown"], "Clean shutdown flushes queue");
            Check((long)status["accepted_records"] == (long)status["written_records"], "Accepted records drain exactly once");
            Check((long)status["dropped_states"] > 0, "Loss count persisted");
            Check((long)status["queued_utf16_bytes"] == 0, "Drained writer releases payload reservations");
            using (var limited = new BundleWriter(Path.Combine(directory, "payload-limit"), 16, 1024))
            {
                Check(!limited.Enqueue("states", new string('x', 513)), "Oversized motion payload cannot exceed queue byte limit");
                Check(limited.DroppedStates == 1, "Payload rejection reports state loss");
                Check(limited.Enqueue("events", "{}"), "Rejected payload releases reservation for later events");
            }
            File.WriteAllText(Path.Combine(directory, "file"), "not a directory");
            using (var badWriter = new BundleWriter(Path.Combine(directory, "file", "child"), 16))
            {
                System.Threading.Thread.Sleep(100);
                Check(badWriter.Fault != null, "Disk failure surfaced");
                Check(!badWriter.Enqueue("events", "{}"), "Failed writer rejects records");
            }
            foreach (string path in new[] { "/mnt/games/SteamLibrary/steamapps/common/Nuclear Option/BepInEx/core/BepInEx.dll",
                "/mnt/games/SteamLibrary/steamapps/common/Nuclear Option/BepInEx/core/0Harmony.dll" })
                Console.WriteLine(AssemblyName.GetAssemblyName(path).FullName);
            Console.WriteLine("PASS: synthetic serialization, independent missiles, client observations, ambiguous/reused IDs, mission reset, unknown outcomes, count/payload queue bounds, flush, disk failure");
            return 0;
        }
        finally { Directory.Delete(directory, true); }
    }
}
