#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NuclearReplay.Shared
{
    // The ffmpeg build Nuclear Replay downloads, and the code that checks and unpacks it. Compiled into the replay
    // plugin (netstandard2.1, the game's Mono) and into NuclearReplay.Core, so nr and the in-game viewer use
    // the same pin, the same install folder and the same checks.
    //
    // Nuclear Replay does not ship ffmpeg. The player's own computer downloads it from the upstream publisher, and only
    // after the player asked for it (nr install, nr ffmpeg install, or "Yes" in the viewer's prompt).
    //
    // The pin: Gyan Doshi's static Windows x64 "full" build of FFmpeg 9.0.2, from the versioned GitHub release
    // https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2. Versioned releases there are kept (4.3.1 from 2020 is still
    // up), unlike BtbN's autobuilds, which are pruned. The full build has every encoder the plugin uses: h264_nvenc,
    // h264_vulkan (GPU under Proton), libx264 and aac (README.txt in the zip, "Enabled encoders"). The essentials build
    // (9.0.2, checked 2026-10-12) is 115 MB instead of 258 MB but has neither h264_vulkan nor any Vulkan support
    // (no --enable-vulkan, so no -init_hw_device vulkan / hwupload to Vulkan), which viewer clips need under Proton. To move to another build, change URL, size and SHA-256 together and download it once to check them.
    internal sealed class FfmpegPackage
    {
        public static readonly FfmpegPackage Pinned = new FfmpegPackage
        {
            Version = "9.0.2",
            Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build.zip",
            Size = 257736868,
            Sha256 = "759d0a9831c436a0eb331ad36f236c06cb04aaa0005da46571f0e9d3d9206f6b",
            ExeEntry = "ffmpeg-9.0.2-full_build/bin/ffmpeg.exe",
            ExeSize = 227823104,
            LicenseEntry = "ffmpeg-9.0.2-full_build/LICENSE",
            Publisher = "gyan.dev (GyanD/codexffmpeg on GitHub)",
            SourceUrl = "https://github.com/FFmpeg/FFmpeg/commit/946fcce07b",
        };

        public string Version { get; set; }
        // HTTPS URL of the zip. GitHub redirects release downloads to its file host; both clients follow redirects.
        public string Url { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        // Zip entries copied out: ffmpeg.exe and the build's license text. Nothing else is unpacked.
        public string ExeEntry { get; set; }
        public long ExeSize { get; set; }
        public string LicenseEntry { get; set; }
        public string Publisher { get; set; }
        public string SourceUrl { get; set; }

        public string Host
        {
            get
            {
                try { return new Uri(Url).Host; }
                catch (UriFormatException) { return Url; }
            }
        }

        // "258 MB": the download size for prompts.
        public string SizeText => Math.Round(Size / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";

        // ---------- where it goes ----------

        // <game>/NuclearReplay/tools/ffmpeg: the mod's data folder, which mod managers leave alone when they replace or
        // remove BepInEx/plugins/NuclearReplay. nr and the replay plugin both look here.
        public static string InstallDir(string gameDir) => Path.Combine(ModFolders.DataDir(gameDir), "tools", "ffmpeg");
        public static string ExePath(string gameDir) => Path.Combine(InstallDir(gameDir), "ffmpeg.exe");

        // The copy an install from before the rename downloaded (<game>/NuclearContext/tools/ffmpeg). Used when the new
        // folder has none, so nobody downloads 258 MB twice. Downloads always go to InstallDir.
        public static string LegacyExePath(string gameDir) => Path.Combine(ModFolders.LegacyDataDir(gameDir), "tools", "ffmpeg", "ffmpeg.exe");

        // The downloaded ffmpeg.exe that exists: the new folder first, then the old one. Null when neither exists.
        public static string FindExe(string gameDir)
        {
            var current = ExePath(gameDir);
            if (File.Exists(current)) return current;
            var legacy = LegacyExePath(gameDir);
            return File.Exists(legacy) ? legacy : null;
        }
        public static string LicensePath(string gameDir) => Path.Combine(InstallDir(gameDir), "LICENSE.txt");

        // A fresh name for a download in progress, so two downloads (nr and the game) never write the same file.
        public static string PartPath(string gameDir) =>
            Path.Combine(InstallDir(gameDir), "download-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip.part");

        // ---------- checks ----------

        // Null when the file has the pinned size and SHA-256; otherwise a plain sentence saying what is wrong.
        public string Verify(string zipPath)
        {
            var info = new FileInfo(zipPath);
            if (!info.Exists) return "The download is missing.";
            if (info.Length != Size)
                return $"The download has the wrong size ({info.Length} bytes, expected {Size}). It was cut short or changed.";
            string actual = Sha256Hex(zipPath);
            if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
                return "The download does not match the expected checksum (SHA-256), so it was not used.";
            return null;
        }

        public static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // Copies ffmpeg.exe and the license out of a verified zip into installDir. Each file is written next to its
        // target first and then renamed, so a reader never sees half an ffmpeg.exe. Throws InvalidDataException when the
        // zip lacks the entries, IOException when a file cannot be written (for example ffmpeg.exe is running).
        public void Extract(string zipPath, string installDir)
        {
            Directory.CreateDirectory(installDir);
            string exe = Path.Combine(installDir, "ffmpeg.exe");
            string license = Path.Combine(installDir, "LICENSE.txt");
            string tag = "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
            using (var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var exeEntry = ZipEntryReader.Find(zip, ExeEntry) ?? throw new InvalidDataException("The download has no " + ExeEntry + ".");
                var licenseEntry = LicenseEntry != null ? ZipEntryReader.Find(zip, LicenseEntry) : null;
                if (ExeSize > 0 && exeEntry.Size != ExeSize)
                    throw new InvalidDataException($"ffmpeg.exe in the download has the wrong size ({exeEntry.Size} bytes, expected {ExeSize}).");
                try
                {
                    ZipEntryReader.CopyTo(zip, exeEntry, exe + tag);
                    if (licenseEntry != null) ZipEntryReader.CopyTo(zip, licenseEntry, license + tag);
                    Replace(exe + tag, exe);
                    if (licenseEntry != null) Replace(license + tag, license);
                }
                finally
                {
                    TryDelete(exe + tag);
                    TryDelete(license + tag);
                }
            }
        }

        // Verifies the downloaded zip, unpacks it into the shared folder of this game and removes the zip. On any problem
        // the zip is removed too (a bad download is never kept) and the exception says why.
        public void InstallFromDownload(string zipPath, string gameDir)
        {
            try
            {
                string problem = Verify(zipPath);
                if (problem != null) throw new InvalidDataException(problem);
                Extract(zipPath, InstallDir(gameDir));
            }
            finally { TryDelete(zipPath); }
        }

        // True when ffmpeg.exe is in the shared folder with the size of the pinned build (a cheap check; the zip was
        // verified when it was installed).
        public bool IsInstalled(string gameDir)
        {
            var info = new FileInfo(ExePath(gameDir));
            return info.Exists && (ExeSize <= 0 || info.Length == ExeSize);
        }

        // Removes downloads that were left behind (the game or nr was closed mid-download).
        public static void CleanPartials(string gameDir)
        {
            try
            {
                var dir = InstallDir(gameDir);
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir, "*.part")) TryDelete(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private static void Replace(string source, string target)
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(source, target);
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }

    // Reads single entries from a zip file: enough for the ffmpeg download (stored or deflated entries, no encryption,
    // no zip64). Written here instead of using ZipArchive so it runs the same in the game's Mono and in .NET.
    internal static class ZipEntryReader
    {
        internal sealed class Entry
        {
            public string Name;
            public int Method;
            public long CompressedSize, Size, LocalHeader;
        }

        private const uint EndOfCentralDirectory = 0x06054b50, CentralHeader = 0x02014b50, LocalHeaderSignature = 0x04034b50;

        public static Entry Find(Stream zip, string name)
        {
            // End of central directory: 22 bytes plus a comment of up to 64 KiB at the end of the file.
            long tail = Math.Min(zip.Length, 22 + 65535);
            var buffer = new byte[tail];
            zip.Seek(zip.Length - tail, SeekOrigin.Begin);
            ReadExactly(zip, buffer, buffer.Length);
            int eocd = -1;
            for (int i = buffer.Length - 22; i >= 0; i--)
                if (U32(buffer, i) == EndOfCentralDirectory) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("The download is not a zip file.");
            int count = U16(buffer, eocd + 10);
            long size = U32(buffer, eocd + 12), offset = U32(buffer, eocd + 16);
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) throw new InvalidDataException("Zip64 files are not supported.");
            if (offset + size > zip.Length) throw new InvalidDataException("The zip file is damaged.");

            var directory = new byte[size];
            zip.Seek(offset, SeekOrigin.Begin);
            ReadExactly(zip, directory, directory.Length);
            int p = 0;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > directory.Length || U32(directory, p) != CentralHeader) throw new InvalidDataException("The zip file is damaged.");
                int flags = U16(directory, p + 8), method = U16(directory, p + 10);
                long compressed = U32(directory, p + 20), uncompressed = U32(directory, p + 24);
                int nameLength = U16(directory, p + 28), extra = U16(directory, p + 30), comment = U16(directory, p + 32);
                long local = U32(directory, p + 42);
                if (p + 46 + nameLength > directory.Length) throw new InvalidDataException("The zip file is damaged.");
                string entryName = Encoding.UTF8.GetString(directory, p + 46, nameLength).Replace('\\', '/');
                if (entryName == name)
                {
                    if ((flags & 1) != 0) throw new InvalidDataException(name + " is encrypted.");
                    if (method != 0 && method != 8) throw new InvalidDataException(name + " uses an unsupported compression method (" + method + ").");
                    return new Entry { Name = entryName, Method = method, CompressedSize = compressed, Size = uncompressed, LocalHeader = local };
                }
                p += 46 + nameLength + extra + comment;
            }
            return null;
        }

        // Writes the entry's uncompressed bytes to target and checks the length.
        public static void CopyTo(Stream zip, Entry entry, string target)
        {
            var header = new byte[30];
            zip.Seek(entry.LocalHeader, SeekOrigin.Begin);
            ReadExactly(zip, header, header.Length);
            if (U32(header, 0) != LocalHeaderSignature) throw new InvalidDataException("The zip file is damaged.");
            long start = entry.LocalHeader + 30 + U16(header, 26) + U16(header, 28);
            if (start + entry.CompressedSize > zip.Length) throw new InvalidDataException("The zip file is damaged.");
            zip.Seek(start, SeekOrigin.Begin);
            long written = 0;
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var data = new Window(zip, entry.CompressedSize);
                Stream source = entry.Method == 8 ? (Stream)new DeflateStream(data, CompressionMode.Decompress, true) : data;
                try
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        written += read;
                        if (written > entry.Size) break;
                    }
                }
                finally { if (source != data) source.Dispose(); }
            }
            if (written != entry.Size) throw new InvalidDataException($"{entry.Name} did not unpack completely ({written} of {entry.Size} bytes).");
        }

        private static void ReadExactly(Stream s, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = s.Read(buffer, done, count - done);
                if (read <= 0) throw new InvalidDataException("The zip file ends too early.");
                done += read;
            }
        }

        private static int U16(byte[] b, int i) => b[i] | b[i + 1] << 8;
        private static uint U32(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);

        // A read-only view of the next `length` bytes of a stream.
        private sealed class Window : Stream
        {
            private readonly Stream inner;
            private long left;
            public Window(Stream inner, long length) { this.inner = inner; left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int read = inner.Read(buffer, offset, (int)Math.Min(count, left));
                left -= read;
                return read;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
