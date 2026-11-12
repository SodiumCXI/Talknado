using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Talknado.Client.Models;

/// <summary>
/// Последний релиз на GitHub, если он новее запущенной версии. Установленная
/// программа обновляется установщиком из релиза; portable открывает страницу
/// релиза: файлы запущенной программы на месте не подменить.
/// </summary>
public sealed record AppUpdate(Version Version, string PageUrl, string? InstallerUrl, string? InstallerSha256)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/SodiumCXI/Talknado/releases/latest";
    private const string InstallerSuffix = "_x64_setup.exe";
    private const int ErrorCancelled = 1223;

    // Соединение с хранилищем GitHub изредка застревает на низкой скорости на всю
    // загрузку, а новое идёт нормально. Медленнее MinBytesPerWindow за SpeedWindow —
    // соединение меняется, загрузка продолжается с того же места.
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    private const long MinBytesPerWindow = 1024 * 1024;
    private const int MaxReconnects = 5;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Рядом с программой лежит деинсталлятор Inno Setup.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>null — запущена последняя версия либо GitHub недоступен.</summary>
    public static async Task<AppUpdate?> FindNewerAsync()
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(LatestReleaseUrl).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var release = json.RootElement;

            if (!Version.TryParse(release.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest)
                || Assembly.GetExecutingAssembly().GetName().Version is not { } current
                || latest <= new Version(current.Major, current.Minor, current.Build))
                return null;

            string? installerUrl = null, sha256 = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString()?.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                installerUrl = asset.GetProperty("browser_download_url").GetString();
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value
                    && value.StartsWith("sha256:", StringComparison.Ordinal))
                    sha256 = value["sha256:".Length..];
            }

            return new AppUpdate(latest, release.GetProperty("html_url").GetString()!, installerUrl, sha256);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public void OpenPage() => Process.Start(new ProcessStartInfo(PageUrl) { UseShellExecute = true });

    /// <summary>
    /// Скачивает установщик и запускает его без вопросов, с окном прогресса. Программу
    /// после установки запускает сам установщик. false — пользователь отказал в UAC.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<int> progress)
    {
        // Имя одно на все версии: в папке временных файлов остаётся не больше одного установщика.
        string path = Path.Combine(Path.GetTempPath(), "Talknado_update" + InstallerSuffix);
        await DownloadAsync(path, progress).ConfigureAwait(false);

        try
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private async Task DownloadAsync(string path, IProgress<int> progress)
    {
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long done = 0, total = 0;
        int percent = -1;

        for (int reconnects = 0; ; reconnects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, InstallerUrl);
            if (done > 0)
                request.Headers.Range = new RangeHeaderValue(done, null);

            using var stall = new CancellationTokenSource();
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Сервер не продолжил с места — загрузка с начала
            if (done > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                file.SetLength(0);
                hash.GetHashAndReset();
                done = 0;
            }
            if (done == 0)
                total = response.Content.Headers.ContentLength ?? 0;

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            bool slow = false;
            long windowStart = Stopwatch.GetTimestamp(), windowBytes = 0;
            while (true)
            {
                int read;
                if (reconnects < MaxReconnects)
                    stall.CancelAfter(SpeedWindow);
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    slow = true;
                    break;
                }
                if (read == 0)
                    break;

                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                windowBytes += read;

                // Не чаще раза на процент: каждый отчёт — обращение к потоку окна.
                if (total > 0 && done * 100 / total != percent)
                {
                    percent = (int)(done * 100 / total);
                    progress.Report(percent);
                }

                if (Stopwatch.GetElapsedTime(windowStart) >= SpeedWindow)
                {
                    if (windowBytes < MinBytesPerWindow && reconnects < MaxReconnects && total - done > MinBytesPerWindow)
                    {
                        slow = true;
                        break;
                    }
                    windowStart = Stopwatch.GetTimestamp();
                    windowBytes = 0;
                }
            }

            if (!slow)
                break;
        }

        if (InstallerSha256 != null
            && !Convert.ToHexString(hash.GetHashAndReset()).Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer hash mismatch");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // Без User-Agent API GitHub отвечает 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Talknado");
        return client;
    }
}
