class MultiThreadDownloader
{
    private static readonly HttpClient client = new HttpClient();

    public static async Task DownloadFileAsync(string url, string outputPath, int threads = 8)
    {
        // Get file size
        var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        headResponse.EnsureSuccessStatusCode();
        long totalSize = headResponse.Content.Headers.ContentLength ?? throw new Exception("Unknown file size");

        long baseChunkSize = totalSize / threads;
        long[] downloadedPerThread = new long[threads];
        DateTime startTime = DateTime.UtcNow;

        async Task DownloadRange(int threadId, long start, long end)
        {
            string partFile = $"{outputPath}.part{threadId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var output = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[8192];
            int bytesRead;
            long totalBytes = end - start + 1;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await output.WriteAsync(buffer, 0, bytesRead);
                Interlocked.Add(ref downloadedPerThread[threadId], bytesRead);

                double percent = (double)downloadedPerThread[threadId] / totalBytes * 100;
                double elapsedSec = (DateTime.UtcNow - startTime).TotalSeconds;
                double speedMBps = (downloadedPerThread[threadId] / 1024d / 1024d) / elapsedSec;

                DrawProgress(threadId, percent, speedMBps, downloadedPerThread, totalSize, startTime);
            }
        }

        // Step 1: Download all parts
        var tasks = Enumerable.Range(0, threads).Select(i =>
        {
            long start = i * baseChunkSize;
            long end = (i == threads - 1) ? totalSize - 1 : (start + baseChunkSize - 1);
            return DownloadRange(i, start, end);
        }).ToList();

        await Task.WhenAll(tasks);

        // Step 2: Merge parts (with progress at the bottom)
        int mergeLine = threads + 9; // place merging progress below all UI
        Console.SetCursorPosition(0, mergeLine);
        Console.WriteLine("\nMerging parts into final file...");

        using (var finalFile = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
        {
            long mergedBytes = 0;
            byte[] buffer = new byte[65536];

            for (int i = 0; i < threads; i++)
            {
                string partFile = $"{outputPath}.part{i}";
                using var partStream = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);

                int bytesRead;
                while ((bytesRead = await partStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await finalFile.WriteAsync(buffer, 0, bytesRead);
                    mergedBytes += bytesRead;

                    if (mergedBytes % (buffer.Length * 10) == 0 || bytesRead < buffer.Length)
                    {
                        double mergePercent = (double)mergedBytes / totalSize * 100;
                        DrawMergeProgress(mergePercent, mergeLine + 1); // Pass target line
                    }
                }
                partStream.Close();
                File.Delete(partFile);
            }

            DrawMergeProgress(100.0, mergeLine + 1);
        }

        Console.WriteLine("\nMerge complete.");
    }

    private static void DrawProgress(int threadId, double percent, double speedMBps, long[] downloadedPerThread, long totalSize, DateTime startTime, int barLength = 40)
    {
        lock (Console.Out)
        {
            long totalDownloaded = downloadedPerThread.Sum();
            double elapsedSec = (DateTime.UtcNow - startTime).TotalSeconds;
            double totalSpeedMBps = (totalDownloaded / 1024d / 1024d) / elapsedSec;
            double remainingBytes = totalSize - totalDownloaded;
            double etaSec = (totalSpeedMBps > 0) ? (remainingBytes / 1024d / 1024d) / totalSpeedMBps : 0;
            TimeSpan eta = TimeSpan.FromSeconds(etaSec);

            int innerWidth = 50;

            string line1 = $" Total Size: {FormatSize(totalSize)}".PadRight(innerWidth);
            string line2 = $" Downloaded: {FormatSize(totalDownloaded)}".PadRight(innerWidth);
            string line3 = $" Speed: {totalSpeedMBps:F2} MB/s".PadRight(innerWidth);
            string line4 = $" Time Left: {eta:hh\\:mm\\:ss}".PadRight(innerWidth);

            string topBorder = "╔" + new string('═', innerWidth) + "╗";
            string bottomBorder = "╚" + new string('═', innerWidth) + "╝";

            Console.SetCursorPosition(0, 0);
            Console.WriteLine(topBorder);
            Console.WriteLine($"║{line1}║");
            Console.WriteLine($"║{line2}║");
            Console.WriteLine($"║{line3}║");
            Console.WriteLine($"║{line4}║");
            Console.WriteLine(bottomBorder);

            // Draw total progress bar (below all threads)
            double totalPercent = (double)totalDownloaded / totalSize * 100;
            Console.SetCursorPosition(0, 6);
            int totalFilled = (int)(barLength * totalPercent / 100);
            string totalBar = new string('#', totalFilled) + new string('-', barLength - totalFilled);
            Console.Write($"Total:  [{totalBar}] {totalPercent:F2}%");

            // Draw per-thread progress bars (start after the box at line 6)
            Console.SetCursorPosition(0, threadId + 7);
            int filled = (int)(barLength * percent / 100);
            string bar = new string('#', filled) + new string('-', barLength - filled);
            Console.Write($"Thread {threadId + 1}: [{bar}] {percent:F2}% {speedMBps:F2} MB/s   ");
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

    private static void DrawMergeProgress(double percent, int line, int barLength = 40)
    {
        lock (Console.Out)
        {
            Console.SetCursorPosition(0, line); // Always draw at fixed position
            int filled = (int)(barLength * percent / 100);
            string bar = new string('#', filled) + new string('-', barLength - filled);
            Console.Write($"Merging: [{bar}] {percent:F2}%   ");
        }
    }

    public static void Main(string[] args)
    {
        Run().GetAwaiter().GetResult();
    }

    private static async Task Run()
    {
        string url = "";
        while (true)
        {
            Console.Write("Enter file URL to download (or type 'exit' to quit): ");
            url = Console.ReadLine()?.Trim() ?? "";

            if (url.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("No URL provided. Try again.");
                continue;
            }

            string outputPath = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = "downloaded.file";

            Console.Clear();
            Console.CursorVisible = false;
            await DownloadFileAsync(url, outputPath, 8);
            Console.CursorVisible = true;

            Console.WriteLine("\nDownload complete. Press Enter to continue...");
            Console.ReadLine();
            Console.Clear();
        }
    }
}
