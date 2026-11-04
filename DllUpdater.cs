using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivDlssFix;

/// <summary>
/// Updates the game's nvngx_dlss.dll (FFXIV ships 3.5.10) from NVIDIA's official DLSS SDK repository,
/// github.com/NVIDIA/DLSS: the DLL lives in the repo at lib/Windows_x86_64/rel/nvngx_dlss.dll, at each release tag.
///
/// Only runs when the player asks. Before anything is installed the download must match
///  * the size and git blob SHA-1 the GitHub API reports for that file at that tag,
///  * a valid Authenticode signature (WinVerifyTrust) from NVIDIA Corporation,
///  * the file version of the release tag.
/// The DLL in use can't be overwritten, but it can be renamed: the old one becomes nvngx_dlss.dll.&lt;version&gt;.bak and
/// the new one takes its name. The game loads it on the next start.
/// </summary>
internal sealed partial class DllUpdater
{
    private const string Repo = "NVIDIA/DLSS";
    private const string RepoPath = "lib/Windows_x86_64/rel/nvngx_dlss.dll";
    private const string DllName = "nvngx_dlss.dll";

    private static readonly HttpClient Http = CreateClient();

    private readonly string gameDir;
    private readonly FileLog log;
    private int busy;

    public DllUpdater(string gameDir, FileLog log)
    {
        this.gameDir = gameDir;
        this.log = log;
    }

    public string State { get; private set; } = string.Empty;

    public bool Busy => Volatile.Read(ref this.busy) != 0;

    /// <summary>Latest release found by the last check, e.g. "310.9.16" (empty before a check).</summary>
    public string LatestVersion { get; private set; } = string.Empty;

    public long LatestSize { get; private set; }

    public bool UpdateAvailable { get; private set; }

    /// <summary>Set after an install; the game keeps using the old DLL until it restarts.</summary>
    public bool RestartNeeded { get; private set; }

    private string LatestTag { get; set; } = string.Empty;

    private string LatestSha { get; set; } = string.Empty;

    private string LatestUrl { get; set; } = string.Empty;

    /// <summary>Version of nvngx_dlss.dll in the game folder ("" if missing).</summary>
    public string InstalledVersion()
    {
        var path = Path.Combine(this.gameDir, DllName);
        return File.Exists(path) ? FileVersion(path) : string.Empty;
    }

    public void CheckAsync() => this.Run(this.CheckCore);

    public void InstallAsync() => this.Run(async ct =>
    {
        if (string.IsNullOrEmpty(this.LatestTag))
            await this.CheckCore(ct);
        if (!string.IsNullOrEmpty(this.LatestTag))
            await this.InstallCore(ct);
    });

    private void Run(Func<CancellationToken, Task> work)
    {
        if (Interlocked.Exchange(ref this.busy, 1) == 1)
            return;
        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await work(cts.Token);
            }
            catch (Exception e)
            {
                this.State = $"Failed: {e.Message}";
                this.log.Error($"DLSS DLL update: {e}");
            }
            finally
            {
                Volatile.Write(ref this.busy, 0);
            }
        });
    }

    private async Task CheckCore(CancellationToken ct)
    {
        this.State = "Checking NVIDIA's DLSS releases on GitHub...";
        using (var rel = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct)))
            this.LatestTag = rel.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(this.LatestTag))
            throw new InvalidOperationException("no release tag");

        using (var file = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/contents/{RepoPath}?ref={Uri.EscapeDataString(this.LatestTag)}", ct)))
        {
            var root = file.RootElement;
            this.LatestSha = root.GetProperty("sha").GetString() ?? string.Empty;
            this.LatestSize = root.GetProperty("size").GetInt64();
            this.LatestUrl = root.GetProperty("download_url").GetString() ?? string.Empty;
        }

        if (!this.LatestUrl.StartsWith("https://raw.githubusercontent.com/NVIDIA/DLSS/", StringComparison.Ordinal) || this.LatestSha.Length != 40 || this.LatestSize < 1_000_000)
            throw new InvalidOperationException($"unexpected file info from GitHub ({this.LatestUrl}, {this.LatestSize} bytes)");

        this.LatestVersion = this.LatestTag.TrimStart('v', 'V');
        var installed = this.InstalledVersion();
        this.UpdateAvailable = !SameOrNewer(installed, this.LatestVersion);
        this.State = this.UpdateAvailable
            ? $"NVIDIA's latest is {this.LatestVersion} ({this.LatestSize / 1048576.0:0} MB); the game folder has {(installed.Length > 0 ? installed : "none")}."
            : $"Up to date: the game folder has {installed}, NVIDIA's latest release is {this.LatestVersion}.";
        this.log.Info($"DLSS DLL check: installed={installed} latest={this.LatestTag} size={this.LatestSize} sha={this.LatestSha}");
    }

    private async Task InstallCore(CancellationToken ct)
    {
        var target = Path.Combine(this.gameDir, DllName);
        var temp = target + ".download";
        try
        {
            // 1. download (same folder, so the final rename is atomic)
            this.State = "Downloading...";
            using (var resp = await Http.GetAsync(this.LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done > this.LatestSize + 1)
                        throw new InvalidOperationException("download is larger than GitHub reported");
                    this.State = $"Downloading {done / 1048576.0:0} / {this.LatestSize / 1048576.0:0} MB...";
                }
            }

            // 2. verify: size + git blob hash, NVIDIA signature, version
            this.State = "Verifying...";
            var bytes = await File.ReadAllBytesAsync(temp, ct);
            if (bytes.LongLength != this.LatestSize)
                throw new InvalidOperationException($"size mismatch: got {bytes.LongLength}, expected {this.LatestSize}");
            var sha = GitBlobSha1(bytes);
            if (!string.Equals(sha, this.LatestSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"hash mismatch: got {sha}, expected {this.LatestSha}");
            var signer = VerifySignature(temp);
            var version = FileVersion(temp);
            if (!SameOrNewer(version, this.LatestVersion) || !SameOrNewer(this.LatestVersion, version))
                throw new InvalidOperationException($"file version {version} doesn't match release {this.LatestVersion}");

            // 3. swap: rename the current DLL (works while the game has it loaded), then move the new one in
            if (File.Exists(target))
            {
                var old = FileVersion(target);
                var backup = Path.Combine(this.gameDir, $"{DllName}.{(old.Length > 0 ? old : "old")}.bak");
                if (File.Exists(backup))
                    backup = Path.Combine(this.gameDir, $"{DllName}.{(old.Length > 0 ? old : "old")}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
                File.Move(target, backup);
                this.log.Info($"DLSS DLL update: kept the previous DLL as {Path.GetFileName(backup)}");
            }

            File.Move(temp, target);
            this.UpdateAvailable = false;
            this.RestartNeeded = true;
            this.State = $"Installed nvngx_dlss.dll {version}, signed by {signer}. Restart the game to use it.";
            this.log.Info($"DLSS DLL update: installed {version} from {this.LatestUrl} (git sha {sha}, signer {signer})");
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XivDlssFix-Dalamud-plugin");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static string FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>True when <paramref name="have"/> is the same as or newer than <paramref name="want"/>.</summary>
    private static bool SameOrNewer(string have, string want)
    {
        if (!Version.TryParse(Pad(have), out var h) || !Version.TryParse(Pad(want), out var w))
            return false;
        return h >= w;

        static string Pad(string s) => s.Split('.').Length == 1 ? s + ".0" : s;
    }

    /// <summary>Git's blob id: SHA-1 over "blob &lt;size&gt;\0" + content (what the GitHub contents API reports as sha).</summary>
    private static string GitBlobSha1(byte[] content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes($"blob {content.LongLength}\0"));
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>WinVerifyTrust on the file, then checks that the Authenticode signer is NVIDIA. Returns the signer subject.</summary>
    private static unsafe string VerifySignature(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        fixed (char* p = path)
        {
            var file = new WintrustFileInfo { CbStruct = (uint)sizeof(WintrustFileInfo), FilePath = p };
            var data = new WintrustData
            {
                CbStruct = (uint)sizeof(WintrustData),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = &file,
                StateAction = 0,
            };
            var hr = WinVerifyTrust(-1, &action, &data);
            if (hr != 0)
                throw new InvalidOperationException($"signature check failed (WinVerifyTrust 0x{hr:X8})");
        }

#pragma warning disable SYSLIB0057 // no replacement exists for reading an Authenticode signer
        using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        var subject = cert.Subject;
        if (!subject.Contains("O=NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"not signed by NVIDIA: {subject}");
        return "NVIDIA Corporation";
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WintrustFileInfo
    {
        public uint CbStruct;
        public char* FilePath;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WintrustData
    {
        public uint CbStruct;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WintrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* action, WintrustData* data);
}
