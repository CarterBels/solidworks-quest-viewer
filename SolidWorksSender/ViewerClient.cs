using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksQuestSender;

public sealed class ViewerException : Exception
{
    public bool Unreachable, WrongCode;
    public ViewerException(string message) : base(message) { }
}

/// The Quest viewer's HTTP protocol (same as the upstream Chrome extension):
///   GET /status, POST /notify, POST /model (binary glTF), GET /commands (long poll for voice "send").
public sealed class ViewerClient
{
    static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    readonly string baseUrl;
    readonly string pairCode;

    public ViewerClient(Settings s)
    {
        baseUrl = "http://" + (s.Viewer ?? "").Trim().Replace("http://", "").Replace("https://", "").TrimEnd('/');
        pairCode = (s.PairCode ?? "").Trim().ToUpperInvariant();
    }

    public string Address => baseUrl.Substring("http://".Length);

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, baseUrl + path);
        r.Headers.TryAddWithoutValidation("X-Pair-Code", pairCode);
        r.Headers.ConnectionClose = true;
        r.Headers.ExpectContinue = false;
        return r;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct = default)
    {
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            HttpResponseMessage res;
            try
            {
                res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                throw new ViewerException($"Viewer not reachable at {Address}. Check the address shown on the keypad and that the Quest is on the same network.") { Unreachable = true };
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                res.Dispose();
                throw new ViewerException("Wrong pairing code. Enter the code shown on the viewer's keypad in Quest Settings.") { WrongCode = true };
            }
            return res;
        }
    }

    static object TryParse(string json)
    {
        try { return Json.Parse(json); } catch (Exception) { return null; }
    }

    /// Returns the key of the model the viewer is showing (or null).
    public async Task<string> StatusAsync()
    {
        using (var res = await SendAsync(Request(HttpMethod.Get, "/status"), TimeSpan.FromSeconds(4)).ConfigureAwait(false))
        {
            var status = TryParse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            // Our viewer build gives every part a cheap collider; the upstream one needs big models grouped.
            Grouping.ViewerHandlesManyParts = Json.Get(status, "manyParts") is bool b && b;
            return Json.Get(status, "key") as string;
        }
    }

    public async Task NotifyAsync(string state, string message)
    {
        try
        {
            var req = Request(HttpMethod.Post, "/notify");
            req.Content = new StringContent("{\"state\":" + Json.Str(state) + ",\"message\":" + Json.Str(message) + "}", Encoding.UTF8, "application/json");
            using (await SendAsync(req, TimeSpan.FromSeconds(4)).ConfigureAwait(false)) { }
        }
        catch (Exception) { /* best effort */ }
    }

    /// Uploads the model; returns the viewer's summary line.
    public async Task<string> SendModelAsync(ExportResult r, Action<long, long> progress = null)
    {
        var req = Request(HttpMethod.Post, "/model");
        req.Content = new ProgressContent(r.Glb, progress);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        req.Headers.TryAddWithoutValidation("X-Model-Name", Uri.EscapeDataString(r.Name));
        req.Headers.TryAddWithoutValidation("X-Model-Key", Uri.EscapeDataString(r.Key));
        req.Headers.TryAddWithoutValidation("X-Hidden-Parts", r.Hidden.ToString());
        req.Headers.TryAddWithoutValidation("X-Part-Info", r.PartInfo ?? "");
        using (var res = await SendAsync(req, TimeSpan.FromSeconds(120)).ConfigureAwait(false))
        {
            var root = TryParse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            string message = Json.Get(root, "message") as string ?? $"HTTP {(int)res.StatusCode}";
            if (!res.IsSuccessStatusCode || !(Json.Get(root, "ok") is bool ok && ok)) throw new ViewerException(message);
            return message;
        }
    }

    /// Long poll (the viewer holds it up to 20 s). Returns the command names, e.g. ["send"].
    public async Task<string[]> CommandsAsync(CancellationToken ct)
    {
        using (var res = await SendAsync(Request(HttpMethod.Get, "/commands"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
        {
            if (!res.IsSuccessStatusCode) throw new ViewerException($"HTTP {(int)res.StatusCode}");
            var list = Json.Get(TryParse(await res.Content.ReadAsStringAsync().ConfigureAwait(false)), "commands") as List<object>;
            return list?.OfType<string>().ToArray() ?? new string[0];
        }
    }
}

/// The model bytes, written in 64 KB pieces so the add-in can show how much has gone (sent, total).
sealed class ProgressContent : HttpContent
{
    readonly byte[] data;
    readonly Action<long, long> progress;

    public ProgressContent(byte[] data, Action<long, long> progress)
    {
        this.data = data;
        this.progress = progress;
    }

    protected override async Task SerializeToStreamAsync(System.IO.Stream stream, TransportContext context)
    {
        const int Chunk = 64 * 1024;
        for (int sent = 0; sent < data.Length;)
        {
            int n = Math.Min(Chunk, data.Length - sent);
            await stream.WriteAsync(data, sent, n).ConfigureAwait(false);
            sent += n;
            progress?.Invoke(sent, data.Length);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = data.Length;
        return true;
    }
}
