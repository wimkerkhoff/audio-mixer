using System.Net;
using System.Text;
using System.Threading;

namespace AudioMixer.Services;

// Tiny loopback-only HTTP server that serves a live JSON snapshot of the mixer state at GET /state.
// Off unless AUDIOMIXER_STATE is set (mirrors AUDIOMIXER_LOG). Read-only — purely diagnostic. Binds
// to 127.0.0.1 only (no URL ACL / admin needed for an explicit loopback prefix).
public sealed class StateServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string> _stateJson;
    private readonly Thread _thread;
    private volatile bool _running;

    public int Port { get; }

    public StateServer(int port, Func<string> stateJson)
    {
        Port = port;
        _stateJson = stateJson;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _thread = new Thread(Loop) { IsBackground = true, Name = "StateServer" };
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; } // listener stopped/disposed

            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                string body;
                if (path is "/" or "/state")
                {
                    body = _stateJson();
                    ctx.Response.StatusCode = 200;
                }
                else
                {
                    body = "{\"error\":\"not found\"}";
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                // Silently returning an empty 200 made a throwing snapshot look like an empty mixer,
                // which is exactly the wrong signal on the endpoint used to diagnose a live service.
                Audio.AudioLog.Write($"/state handler failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
