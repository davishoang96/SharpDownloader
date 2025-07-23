using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;

class MultiThreadDownloader
{
    private static readonly HttpClient client = new HttpClient(new HttpClientHandler
    {
        UseCookies = false,
        MaxConnectionsPerServer = 8
    });

    private class ResumeData
    {
        public bool[] CompletedChunks { get; set; } = Array.Empty<bool>();
        public long ChunkSize { get; set; }
        public long TotalSize { get; set; }
        public Dictionary<string, long> PartialOffsets { get; set; } = new();
    }

    private static readonly Queue<(DateTime Time, long Bytes)> speedSamples = new();
    private static DateTime lastProgressUpdate = DateTime.UtcNow;

    public static async Task DownloadFileAsync(string url, string outputPath, int threads = 8, int initialChunkMB = 8)
    {
        var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        headResponse.EnsureSuccessStatusCode();
        long totalSize = headResponse.Content.Headers.ContentLength ?? throw new Exception("Unknown file size");

        string resumeFile = $"{outputPath}.resume";
        ResumeData resume = new ResumeData { TotalSize = totalSize };
        if (File.Exists(resumeFile))
        {
            resume = JsonSerializer.Deserialize<ResumeData>(await File.ReadAllTextAsync(resumeFile)) ?? resume;
            if (resume.TotalSize != totalSize) resume = new ResumeData { TotalSize = totalSize };
        }

        long chunkSize = resume.ChunkSize > 0 ? resume.ChunkSize : initialChunkMB * 1024L * 1024L;
        if (chunkSize < 8 * 1024 * 1024) chunkSize = 8 * 1024 * 1024;

        int chunkCount = (int)Math.Ceiling((double)totalSize / chunkSize);
        if (resume.CompletedChunks.Length != chunkCount)
            resume.CompletedChunks = new bool[chunkCount];

        if (!File.Exists(outputPath))
        {
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Write))
                fs.SetLength(totalSize);
        }

        long[] downloadedPerThread = new long[threads];
        long totalDownloaded = resume.CompletedChunks
            .Select((done, idx) => done ? Math.Min(chunkSize, totalSize - idx * chunkSize) : 0)
            .Sum();

        DateTime startTime = DateTime.UtcNow;
        object chunkLock = new object();

        var chunks = Enumerable.Range(0, chunkCount)
            .Where(i => !resume.CompletedChunks[i])
            .Select(i =>
            {
                long start = i * chunkSize;
                long end = Math.Min(totalSize - 1, start + chunkSize - 1);
                return (Index: i, Start: start, End: end);
            }).ToList();

        var partialOffsets = resume.PartialOffsets ?? new Dictionary<string, long>();

        async Task Worker(int threadId)
        {
            while (true)
            {
                (int Index, long Start, long End) chunk;
                lock (chunkLock)
                {
                    if (!chunks.Any()) break;
                    chunk = chunks[0];
                    chunks.RemoveAt(0);
                }

                long resumeOffset = chunk.Start;
                string chunkKey = $"Chunk_{chunk.Index}";

                if (partialOffsets.TryGetValue(chunkKey, out long savedOffset))
                    resumeOffset = chunk.Start + savedOffset;

                if (resumeOffset > chunk.End)
                {
                    resume.CompletedChunks[chunk.Index] = true;
                    lock (partialOffsets) partialOffsets.Remove(chunkKey);
                    continue;
                }

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeOffset, chunk.End);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.Write, 1024 * 1024, useAsync: true);
                output.Seek(resumeOffset, SeekOrigin.Begin);

                byte[] buffer = new byte[1024 * 1024]; // 1MB buffer
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead);
                    resumeOffset += bytesRead;

                    Interlocked.Add(ref downloadedPerThread[threadId], bytesRead);
                    Interlocked.Add(ref totalDownloaded, bytesRead);

                    // Track partial offset (relative to chunk start)
                    lock (partialOffsets) partialOffsets[chunkKey] = resumeOffset - chunk.Start;

                    if ((DateTime.UtcNow - lastProgressUpdate).TotalMilliseconds >= 500)
                    {
                        double percent = (double)totalDownloaded / totalSize * 100;
                        double elapsedSec = (DateTime.UtcNow - startTime).TotalSeconds;
                        double avgSpeedMBps = (totalDownloaded / 1024d / 1024d) / elapsedSec;
                        DrawProgress(outputPath, percent, avgSpeedMBps, downloadedPerThread, totalSize, startTime, threads);
                        lastProgressUpdate = DateTime.UtcNow;
                    }
                }

                resume.CompletedChunks[chunk.Index] = true;
                lock (partialOffsets) partialOffsets.Remove(chunkKey);
            }
        }

        var workers = Enumerable.Range(0, threads).Select(id => Worker(id)).ToArray();

        // Periodically save resume state
        var saver = Task.Run(async () =>
        {
            while (!Task.WhenAll(workers).IsCompleted)
            {
                lock (partialOffsets)
                {
                    resume.PartialOffsets = new Dictionary<string, long>(partialOffsets);
                }
                resume.ChunkSize = chunkSize;
                await File.WriteAllTextAsync(resumeFile, JsonSerializer.Serialize(resume));
                await Task.Delay(5000);
            }
        });

        await Task.WhenAll(workers);
        await saver;

        // Mark all chunks complete and delete resume file
        resume.CompletedChunks = Enumerable.Repeat(true, chunkCount).ToArray();
        await File.WriteAllTextAsync(resumeFile, JsonSerializer.Serialize(resume));
        File.Delete(resumeFile);

        Console.SetCursorPosition(0, threads + 8);
        Console.WriteLine("\nDownload complete.");
    }

    private static void DrawProgress(string fileName, double avgSpeedMBps, double totalSpeedMBps, long[] downloadedPerThread, long totalSize, DateTime startTime, int threads, int barLength = 50)
    {
        lock (Console.Out)
        {
            long totalDownloaded = downloadedPerThread.Sum();
            DateTime now = DateTime.UtcNow;

            // Smooth instant speed (last 3 seconds)
            speedSamples.Enqueue((now, totalDownloaded));
            while (speedSamples.Count > 0 && (now - speedSamples.Peek().Time).TotalSeconds > 3)
                speedSamples.Dequeue();

            double instantSpeedMBps = 0;
            if (speedSamples.Count >= 2)
            {
                var first = speedSamples.Peek();
                var last = speedSamples.Last();
                double bytesDelta = last.Bytes - first.Bytes;
                double seconds = (last.Time - first.Time).TotalSeconds;
                instantSpeedMBps = (seconds > 0) ? (bytesDelta / 1024d / 1024d) / seconds : 0;
            }

            long remainingBytes = totalSize - totalDownloaded;
            double etaSec = (instantSpeedMBps > 0) ? (remainingBytes / 1024d / 1024d) / instantSpeedMBps : 0;
            TimeSpan eta = TimeSpan.FromSeconds(etaSec);

            double percent = (double)totalDownloaded / totalSize * 100;

            Console.SetCursorPosition(0, 0);
            Console.WriteLine($" File: {fileName}");
            Console.WriteLine($" Speed: {instantSpeedMBps:F2} MB/s (avg: {avgSpeedMBps:F2} MB/s) | ETA: {eta:hh\\:mm\\:ss} | {FormatSize(totalDownloaded)} / {FormatSize(totalSize)}");

            int filled = (int)(barLength * percent / 100);
            string bar = new string('#', filled) + new string('-', barLength - filled);
            Console.WriteLine($" [{bar}] {percent:F2}%");

            for (int i = 0; i < threads; i++)
            {
                double threadMB = downloadedPerThread[i] / 1024d / 1024d;
                Console.SetCursorPosition(0, 4 + i);
                Console.WriteLine($" Thread {i + 1}: {threadMB:F2} MB downloaded       ");
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static void Main(string[] args)
    {
        Run().GetAwaiter().GetResult();
    }

    private static async Task Run()
    {
        Console.Write("Enter file URL to download: ");
        string url = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return;

        string outputPath = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(outputPath)) outputPath = "downloaded.file";

        Console.Clear();
        Console.CursorVisible = false;
        await DownloadFileAsync(url, outputPath, 8, 8);
        Console.CursorVisible = true;
    }
}
