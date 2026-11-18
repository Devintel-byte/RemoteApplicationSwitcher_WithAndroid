// Show Control receiver for Unity builds.
// Drop this file into Assets/, add the component to a GameObject in your first scene,
// and wire commands to your own methods in the Inspector.
//
// It listens on a UDP port (default 9000) for either:
//   • OSC messages  — agent step: { "action": "osc", "port": 9000, "address": "/game/level", "args": ["next"] }
//   • plain text    — agent step: { "action": "udp", "port": 9000, "text": "reset" }
//                     (text "level next" is treated as command "level" with argument "next")
//
// Built-in commands (can be turned off):
//   /showcontrol/quit             quit the build
//   /showcontrol/reload           reload the active scene
//   /showcontrol/scene <name>     load a scene by name (must be in Build Settings)
//
// Works on Windows/Mac/Linux builds and in the Editor. Unity 2020.3 or newer.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public class ShowControlReceiver : MonoBehaviour
{
    [Serializable]
    public class StringEvent : UnityEvent<string> { }

    [Serializable]
    public class Binding
    {
        [Tooltip("OSC address (/game/level) or text command (reset). Case-insensitive.")]
        public string command = "/game/start";
        [Tooltip("Fired when the command arrives.")]
        public UnityEvent onReceived = new UnityEvent();
        [Tooltip("Fired with the first argument as text (empty if none).")]
        public StringEvent onReceivedWithArg = new StringEvent();
    }

    [Header("Network")]
    [Tooltip("UDP port to listen on. Use the same port in the agent's osc/udp step.")]
    public int port = 9000;
    [Tooltip("Keep running when another component calls DontDestroyOnLoad on scene change.")]
    public bool persistAcrossScenes = true;

    [Header("Commands")]
    public List<Binding> bindings = new List<Binding>();
    [Tooltip("Fired for every message: (command, firstArg). Handy for routing in code.")]
    public UnityEvent<string, string> onAnyMessage;
    public bool enableBuiltInCommands = true;
    public bool logMessages = true;

    /// <summary>Subscribe from code: ShowControlReceiver.Received += (cmd, args) => { ... };</summary>
    public static event Action<string, object[]> Received;

    private readonly ConcurrentQueue<(string command, object[] args)> _queue = new ConcurrentQueue<(string, object[])>();
    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;

    private void Awake()
    {
        if (persistAcrossScenes) DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _running = true;
            _thread = new Thread(Listen) { IsBackground = true, Name = "ShowControlReceiver" };
            _thread.Start();
            if (logMessages) Debug.Log($"[ShowControl] Listening on UDP {port}");
        }
        catch (SocketException e)
        {
            Debug.LogError($"[ShowControl] Could not listen on UDP {port}: {e.Message}");
        }
    }

    private void OnDisable()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
        if (_thread != null && _thread.IsAlive) _thread.Join(200);
        _thread = null;
    }

    private void Listen()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref any);
                if (data.Length == 0) continue;
                if (data[0] == (byte)'/' && TryParseOsc(data, out string address, out object[] oscArgs))
                    _queue.Enqueue((address, oscArgs));
                else if (data[0] == (byte)'#') // OSC bundle: not used by the agent, ignore
                    continue;
                else
                    _queue.Enqueue(ParseText(Encoding.UTF8.GetString(data)));
            }
            catch (SocketException) { if (!_running) break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] Bad packet: {e.Message}"); }
        }
    }

    private void Update()
    {
        // Unity APIs are main-thread only, so messages are handled here.
        while (_queue.TryDequeue(out var msg)) Dispatch(msg.command, msg.args);
    }

    private void Dispatch(string command, object[] args)
    {
        string first = args.Length > 0 ? Convert.ToString(args[0], System.Globalization.CultureInfo.InvariantCulture) : "";
        if (logMessages) Debug.Log($"[ShowControl] {command} {string.Join(" ", args)}");

        if (enableBuiltInCommands && HandleBuiltIn(command, first)) return;

        foreach (var b in bindings)
        {
            if (!string.Equals(b.command?.Trim(), command, StringComparison.OrdinalIgnoreCase)) continue;
            b.onReceived?.Invoke();
            b.onReceivedWithArg?.Invoke(first);
        }
        onAnyMessage?.Invoke(command, first);
        Received?.Invoke(command, args);
    }

    private static bool HandleBuiltIn(string command, string arg)
    {
        switch (command.ToLowerInvariant())
        {
            case "/showcontrol/quit":
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                return true;
            case "/showcontrol/reload":
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                return true;
            case "/showcontrol/scene":
                if (!string.IsNullOrEmpty(arg)) SceneManager.LoadScene(arg);
                return true;
            default:
                return false;
        }
    }

    // ---------- parsing ----------

    private static (string, object[]) ParseText(string text)
    {
        text = text.Trim();
        int space = text.IndexOf(' ');
        if (space < 0) return (text, Array.Empty<object>());
        return (text.Substring(0, space), new object[] { text.Substring(space + 1).Trim() });
    }

    private static bool TryParseOsc(byte[] data, out string address, out object[] args)
    {
        args = Array.Empty<object>();
        int pos = 0;
        address = ReadString(data, ref pos);
        if (address == null) return false;
        if (pos >= data.Length) return true; // no type tags: address only
        string tags = ReadString(data, ref pos);
        if (tags == null || tags.Length == 0 || tags[0] != ',') return false;

        var list = new List<object>();
        for (int i = 1; i < tags.Length; i++)
        {
            switch (tags[i])
            {
                case 'i': list.Add(ReadInt(data, ref pos)); break;
                case 'f': list.Add(ReadFloat(data, ref pos)); break;
                case 's': list.Add(ReadString(data, ref pos) ?? ""); break;
                case 'T': list.Add(true); break;
                case 'F': list.Add(false); break;
                case 'N': list.Add(null); break;
                default: args = list.ToArray(); return true; // unsupported tag: keep what we have
            }
        }
        args = list.ToArray();
        return true;
    }

    private static string ReadString(byte[] d, ref int pos)
    {
        int start = pos;
        while (pos < d.Length && d[pos] != 0) pos++;
        if (pos >= d.Length) return null;
        string s = Encoding.UTF8.GetString(d, start, pos - start);
        pos = (pos + 4) & ~3; // skip terminator and padding
        return s;
    }

    private static float ReadFloat(byte[] d, ref int pos)
    {
        // Works on every Unity API level (.NET Standard 2.0 has no Int32BitsToSingle).
        byte[] b = BitConverter.GetBytes(ReadInt(d, ref pos));
        return BitConverter.ToSingle(b, 0);
    }

    private static int ReadInt(byte[] d, ref int pos)
    {
        if (pos + 4 > d.Length) throw new FormatException("Truncated OSC argument");
        int v = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
        pos += 4;
        return v;
    }
}
