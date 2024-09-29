using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

class Program
{
    private static string baseUrl = "";
    private static string accessToken = "";
    private static readonly string downloadsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
    private static readonly object _consoleLock = new object();
    private static int _currentConsoleLine = 0;
    private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(5);

    static async Task Main(string[] args)
    {
        if (!Directory.Exists(downloadsPath))
        {
            Directory.CreateDirectory(downloadsPath);
        }

        Console.Write("Enter your institution name (e.g., https://<INSTITUTION>.instructure.com/): ");
        baseUrl = Console.ReadLine().Trim();
        
        baseUrl = $"https://{baseUrl}.instructure.com/api/v1/";
        Console.Write("Enter your Canvas API Key: ");
        accessToken = Console.ReadLine().Trim();

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("Base URL and API Key are required.");
            return;
        }

        var courses = await GetCoursesAsync();

        foreach (var course in courses)
        {
            Console.Clear();
            Console.WriteLine($"Downloading files for course: {course.Name} (ID: {course.Id})");
            var files = await GetCourseFilesAsync(course.Id);

            var filteredFiles = files.FindAll(a => a.DisplayName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                                 a.DisplayName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));

            if (filteredFiles.Count > 0)
            {
                string coursePath = Path.Combine(downloadsPath, SanitizeFileName(course.Name));
                if (!Directory.Exists(coursePath))
                {
                    Directory.CreateDirectory(coursePath);
                }

                Console.WriteLine($"Downloading {filteredFiles.Count} files...");

                int consoleLineName;
                int consoleLineProgress;

                lock (_consoleLock)
                {
                    consoleLineName = _currentConsoleLine++;
                    consoleLineProgress = _currentConsoleLine++;
                }

                var tasks = new List<Task>();
                foreach (var file in filteredFiles)
                {
                    await _semaphore.WaitAsync();
                    string fullPath = coursePath;
                    if (!string.IsNullOrEmpty(file.FolderPath))
                    {
                        var subFolders = file.FolderPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var folder in subFolders)
                        {
                            fullPath = Path.Combine(fullPath, SanitizeFileName(folder));
                        }
                    }
                    if (!Directory.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                    var filePath = Path.Combine(fullPath, file.DisplayName);
                    var task = DownloadFileAsync(file, filePath, consoleLineProgress)
                        .ContinueWith(t => _semaphore.Release());
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);

                Console.WriteLine("\nDownload complete!\n");
            }
            else
            {
                Console.WriteLine("No files available for download.\n");
            }
        }

        Console.WriteLine("Process ended.");
    }

    private static async Task<List<Curso>> GetCoursesAsync()
    {
        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage response = await client.GetAsync($"{baseUrl}courses?per_page=100");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Error retrieving course data.");
                return new List<Curso>();
            }

            string json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<Curso>>(json);
        }
    }

    private static async Task<List<Archivo>> GetCourseFilesAsync(int courseId)
    {
        List<Archivo> allFiles = new List<Archivo>();
        Dictionary<int, string> folderPaths = new Dictionary<int, string>();
        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            List<Folder> folders = await GetFoldersAsync(client, courseId);

            foreach (var folder in folders)
            {
                string adjustedFolderPath = folder.FullName;
                string[] pathSegments = adjustedFolderPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathSegments.Length > 0 && pathSegments[0].Equals("course files", StringComparison.OrdinalIgnoreCase))
                {
                    adjustedFolderPath = string.Join("/", pathSegments, 1, pathSegments.Length - 1);
                }
                folderPaths[folder.Id] = adjustedFolderPath;
            }

            foreach (var folder in folders)
            {
                List<Archivo> folderFiles = await GetFilesFromFolderAsync(client, folder.Id);
                foreach (var file in folderFiles)
                {
                    if (folderPaths.TryGetValue(file.FolderId, out string folderPath))
                    {
                        file.FolderPath = folderPath;
                    }
                    else
                    {
                        file.FolderPath = "";
                    }
                    allFiles.Add(file);
                }
            }

            List<Archivo> rootFiles = await GetFilesFromFolderAsync(client, 0);
            foreach (var file in rootFiles)
            {
                file.FolderPath = "";
                allFiles.Add(file);
            }
        }
        return allFiles;
    }

    private static async Task<List<Archivo>> GetFilesFromFolderAsync(HttpClient client, int folderId)
    {
        List<Archivo> files = new List<Archivo>();
        string endpoint = folderId == 0 ? $"{baseUrl}courses/{folderId}/files?per_page=100" : $"{baseUrl}folders/{folderId}/files?per_page=100";
        string url = endpoint;

        while (!string.IsNullOrEmpty(url))
        {
            HttpResponseMessage response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error retrieving files from {url}. Status: {response.StatusCode}");
                break;
            }

            string json = await response.Content.ReadAsStringAsync();
            List<Archivo> pageFiles = JsonConvert.DeserializeObject<List<Archivo>>(json);
            files.AddRange(pageFiles);

            url = GetNextPageUrl(response.Headers);
        }

        return files;
    }

    private static async Task<List<Folder>> GetFoldersAsync(HttpClient client, int courseId)
    {
        List<Folder> allFolders = new List<Folder>();
        string endpoint = $"{baseUrl}courses/{courseId}/folders?per_page=100";
        string url = endpoint;

        while (!string.IsNullOrEmpty(url))
        {
            HttpResponseMessage response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error retrieving folders from {url}. Status: {response.StatusCode}");
                break;
            }

            string json = await response.Content.ReadAsStringAsync();
            List<Folder> pageFolders = JsonConvert.DeserializeObject<List<Folder>>(json);
            allFolders.AddRange(pageFolders);

            url = GetNextPageUrl(response.Headers);
        }

        return allFolders;
    }

    private static string GetNextPageUrl(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Link", out IEnumerable<string> linkValues))
        {
            foreach (var link in linkValues)
            {
                var links = link.Split(',');
                foreach (var l in links)
                {
                    var parts = l.Split(';');
                    if (parts.Length == 2 && parts[1].Trim() == "rel=\"next\"")
                    {
                        string url = parts[0].Trim().Trim('<', '>');
                        return url;
                    }
                }
            }
        }
        return null;
    }

    private static async Task DownloadFileAsync(Archivo archivo, string filePath, int consoleLine)
    {
        try
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(archivo.Url, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var buffer = new byte[8192];
                    var totalDownloadedBytes = 0L;

                    using (var inputStream = await response.Content.ReadAsStreamAsync())
                    using (var outputStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        consoleLine = Console.CursorTop;
                        int readBytes;
                        while ((readBytes = await inputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await outputStream.WriteAsync(buffer, 0, readBytes);
                            totalDownloadedBytes += readBytes;
                            ShowProgressBar(totalDownloadedBytes, totalBytes, archivo.DisplayName, consoleLine);
                        }
                    }

                    File.SetCreationTime(filePath, archivo.CreatedAt);
                    File.SetLastWriteTime(filePath, archivo.UpdatedAt);

                    lock (_consoleLock)
                    {
                        Console.SetCursorPosition(0, consoleLine);
                        Console.WriteLine($"[##################################################] 100.00% - {archivo.DisplayName}                          ");
                    }
                }
                else
                {
                    lock (_consoleLock)
                    {
                        Console.SetCursorPosition(0, consoleLine);
                        Console.WriteLine($"Error downloading {archivo.DisplayName}. Status: {response.StatusCode}                          ");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_consoleLock)
            {
                Console.SetCursorPosition(0, consoleLine);
                Console.WriteLine($"Error while downloading {archivo.DisplayName}: {ex.Message}                      ");
            }
        }
    }

    private static void ShowProgressBar(long downloaded, long total, string fileName, int consoleLine)
    {
        if (total <= 0) return;

        double percentage = (double)downloaded / total * 100;
        int progressBarLength = (int)(percentage / 2);
        string progressText = $"{fileName}";

        lock (_consoleLock)
        {
            int currentLeft = Console.CursorLeft;
            int currentTop = Console.CursorTop;

            Console.SetCursorPosition(0, consoleLine);
            Console.Write($"[{new string('#', progressBarLength)}{new string('-', 50 - progressBarLength)}] {percentage:0.00}% - {progressText}          ");

            Console.SetCursorPosition(currentLeft, currentTop);
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}

public class Curso
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }
}

public class Archivo
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("display_name")]
    public string DisplayName { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("folder_id")]
    public int FolderId { get; set; }

    public string FolderPath { get; set; }
}

public class Folder
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("parent_folder_id")]
    public int? ParentFolderId { get; set; }

    [JsonProperty("full_name")]
    public string FullName { get; set; }
}
