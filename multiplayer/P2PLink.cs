// VBA-M 網際網路 P2P 連線通道
//
// VBA-M 的網路連線是「主機開 TCP 伺服器、其他玩家連進來」。這裡把那條 TCP 連線
// 包進 UDP，經過 NAT 打洞後在兩台電腦之間直接傳送：
//
//   VBA-M(用戶端) -> 127.0.0.1:port -> [本程式] ==UDP P2P==> [本程式] -> 127.0.0.1:port -> VBA-M(伺服器)
//
// 配對碼 -> PBKDF2 -> 信令頻道名稱 + 加密金鑰 + 驗證金鑰。
// 信令（交換位址）透過 ntfy.sh，內容以 AES-256-CBC + HMAC-SHA256 加密；遊戲資料只走 P2P。
// 每個 UDP 封包都以 HMAC 驗證，不知道配對碼的人無法注入資料。
//
// 以 C# 5 撰寫，供 Windows PowerShell 5.1 的 Add-Type 直接編譯。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VbamP2P
{
    public static class Log
    {
        static readonly object lk = new object();

        public static void W(string fmt, params object[] args)
        {
            string s = args.Length > 0 ? string.Format(fmt, args) : fmt;
            lock (lk) Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        }

        public static readonly bool Debug = Environment.GetEnvironmentVariable("VBAMP2P_DEBUG") == "1";

        public static void D(string fmt, params object[] args)
        {
            if (Debug) W("[debug] " + fmt, args);
        }
    }

    public sealed class Keys
    {
        public readonly string Topic;
        readonly byte[] encKey;
        readonly byte[] macKey;
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        // 忽略大小寫、空白與連字號，"abcd-1234" 與 "ABCD 1234" 視為相同
        public static string Normalize(string code)
        {
            var sb = new StringBuilder();
            foreach (char c in code.ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public Keys(string code)
        {
            byte[] pw = Encoding.UTF8.GetBytes(Normalize(code));
            using (var kdf = new Rfc2898DeriveBytes(pw, Encoding.ASCII.GetBytes("vbam-p2p-link-v1"), 50000))
            {
                byte[] k = kdf.GetBytes(80);
                Topic = "vbamp2p-" + BitConverter.ToString(k, 0, 16).Replace("-", "").ToLowerInvariant();
                encKey = Sub(k, 16, 32);
                macKey = Sub(k, 48, 32);
            }
        }

        public static byte[] Random(int n)
        {
            byte[] b = new byte[n];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        public byte[] Mac(byte[] data, int off, int len)
        {
            using (var h = new HMACSHA256(macKey)) return h.ComputeHash(data, off, len);
        }

        public bool VerifyMac(byte[] data, int len, int tagLen)
        {
            byte[] tag = Mac(data, 0, len - tagLen);
            int diff = 0;
            for (int i = 0; i < tagLen; i++) diff |= tag[i] ^ data[len - tagLen + i];
            return diff == 0;
        }

        public string Seal(string plain)
        {
            using (var aes = new RijndaelManaged())
            {
                aes.Key = encKey;
                aes.GenerateIV();
                byte[] pt = Encoding.UTF8.GetBytes(plain);
                byte[] ct;
                using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(pt, 0, pt.Length);
                byte[] body = new byte[16 + ct.Length + 32];
                Buffer.BlockCopy(aes.IV, 0, body, 0, 16);
                Buffer.BlockCopy(ct, 0, body, 16, ct.Length);
                Buffer.BlockCopy(Mac(body, 0, 16 + ct.Length), 0, body, 16 + ct.Length, 32);
                return Convert.ToBase64String(body);
            }
        }

        public string Open(string b64)
        {
            try
            {
                byte[] body = Convert.FromBase64String(b64);
                if (body.Length < 16 + 16 + 32 || !VerifyMac(body, body.Length, 32)) return null;
                using (var aes = new RijndaelManaged())
                {
                    aes.Key = encKey;
                    aes.IV = Sub(body, 0, 16);
                    using (var d = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(d.TransformFinalBlock(body, 16, body.Length - 16 - 32));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static byte[] Sub(byte[] b, int off, int len)
        {
            byte[] r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }
    }

    // 透過 ntfy.sh 交換加密的信令訊息（只在建立連線時使用）
    public sealed class Signal
    {
        const string BaseUrl = "https://ntfy.sh/";
        // 只接受 30 秒內的訊息，避免讀到上一次的舊訊息
        const int MaxAgeSeconds = 30;

        readonly Keys keys;
        readonly string url;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly HashSet<string> seen = new HashSet<string>();
        public Action<Dictionary<string, string>> OnMessage;

        public Signal(Keys k)
        {
            keys = k;
            url = BaseUrl + k.Topic;
        }

        public static long UnixNow()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public void Start()
        {
            var t = new Thread(Listen);
            t.IsBackground = true;
            t.Start();
        }

        void Listen()
        {
            long since = UnixNow() - MaxAgeSeconds;
            int backoff = 1000;
            while (true)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url + "/json?since=" + since);
                    req.Timeout = 15000;
                    req.ReadWriteTimeout = 120000; // ntfy 每 45 秒送一次 keepalive
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        backoff = 1000;
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            var ev = json.DeserializeObject(line) as Dictionary<string, object>;
                            if (ev == null || !"message".Equals(ev["event"])) continue;
                            string id = ev["id"].ToString();
                            if (!seen.Add(id)) continue;
                            since = Math.Max(since, Convert.ToInt64(ev["time"]));
                            object body;
                            if (!ev.TryGetValue("message", out body)) continue;
                            string plain = keys.Open(body.ToString());
                            if (plain == null) continue;
                            var msg = json.Deserialize<Dictionary<string, string>>(plain);
                            string ts;
                            if (!msg.TryGetValue("ts", out ts) || Math.Abs(UnixNow() - long.Parse(ts)) > MaxAgeSeconds) continue;
                            if (OnMessage != null) OnMessage(msg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.W("信令連線中斷，{0} 秒後重試：{1}", backoff / 1000, ex.Message);
                }
                Thread.Sleep(backoff);
                backoff = Math.Min(backoff * 2, 15000);
            }
        }

        public void Send(Dictionary<string, string> msg)
        {
            msg["ts"] = UnixNow().ToString();
            string body = keys.Seal(json.Serialize(msg));
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.UploadString(url, "POST", body);
                }
            }
            catch (Exception ex)
            {
                Log.W("信令傳送失敗：{0}", ex.Message);
            }
        }
    }

    public static class Net
    {
        static readonly string[] StunServers = { "stun.l.google.com:19302", "stun1.l.google.com:19302", "stun.cloudflare.com:3478" };
        const int SIO_UDP_CONNRESET = -1744830452;

        // IPv6 雙堆疊 socket（可同時收發 IPv4/IPv6），沒有 IPv6 時退回 IPv4
        public static Socket NewUdp(out bool v6)
        {
            Socket s;
            try
            {
                s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                s.DualMode = true;
                s.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                v6 = true;
            }
            catch (Exception)
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                v6 = false;
            }
            // 打洞時對方未開的位址會回 ICMP，Windows 預設會讓後續 ReceiveFrom 拋例外
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch (Exception) { }
            return s;
        }

        public static IPEndPoint Norm(IPEndPoint ep)
        {
            if (ep.Address.IsIPv4MappedToIPv6) return new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
            return ep;
        }

        public static IPEndPoint Target(Socket s, IPEndPoint ep)
        {
            if (s.AddressFamily == AddressFamily.InterNetworkV6 && ep.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
            return ep;
        }

        static bool IsUsableV6(IPAddress a)
        {
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6Teredo || IPAddress.IPv6Loopback.Equals(a)) return false;
            byte b0 = a.GetAddressBytes()[0];
            return (b0 & 0xFE) != 0xFC; // 排除 ULA fc00::/7
        }

        public static List<IPEndPoint> Gather(Socket s, bool v6)
        {
            int port = ((IPEndPoint)s.LocalEndPoint).Port;
            var list = new List<IPEndPoint>();
            bool hasV6 = false;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (a.GetAddressBytes()[0] == 169) continue; // APIPA
                        list.Add(new IPEndPoint(a, port));
                    }
                    else if (v6 && a.AddressFamily == AddressFamily.InterNetworkV6 && IsUsableV6(a))
                    {
                        list.Add(new IPEndPoint(new IPAddress(a.GetAddressBytes()), port));
                        hasV6 = true;
                    }
                }
            }

            var pub4 = StunQuery(s, AddressFamily.InterNetwork);
            if (pub4 != null && !list.Contains(pub4)) list.Insert(0, pub4);
            // 對稱型 NAT 對每個目的地分配不同外部埠，但常會盡量沿用本機埠號；多給對方一個猜測位址
            if (pub4 != null && pub4.Port != port) list.Insert(1, new IPEndPoint(pub4.Address, port));
            if (hasV6)
            {
                var pub6 = StunQuery(s, AddressFamily.InterNetworkV6);
                if (pub6 != null && !list.Contains(pub6)) list.Insert(0, pub6);
            }
            if (pub4 == null) Log.W("警告：無法取得公開 IPv4 位址（STUN 失敗），只能嘗試 IPv6／區域網路");
            return list;
        }

        public static string Join(List<IPEndPoint> eps)
        {
            var parts = new List<string>();
            foreach (var ep in eps) parts.Add(ep.ToString());
            return string.Join(";", parts.ToArray());
        }

        public static List<IPEndPoint> Split(string s)
        {
            var list = new List<IPEndPoint>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split(';'))
            {
                try
                {
                    int i = part.LastIndexOf(':');
                    var addr = IPAddress.Parse(part.Substring(0, i).Trim('[', ']'));
                    list.Add(new IPEndPoint(addr, int.Parse(part.Substring(i + 1))));
                }
                catch (Exception) { }
            }
            return list;
        }

        public static IPEndPoint StunQuery(Socket s, AddressFamily fam)
        {
            foreach (var hp in StunServers)
            {
                try
                {
                    string[] parts = hp.Split(':');
                    IPAddress addr = null;
                    foreach (var a in Dns.GetHostAddresses(parts[0]))
                        if (a.AddressFamily == fam) { addr = a; break; }
                    if (addr == null) continue;
                    var server = new IPEndPoint(addr, int.Parse(parts[1]));

                    byte[] req = new byte[20];
                    req[1] = 0x01; // Binding Request
                    req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
                    byte[] tx = Keys.Random(12);
                    Buffer.BlockCopy(tx, 0, req, 8, 12);

                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        s.SendTo(req, Target(s, server));
                        DateTime until = DateTime.UtcNow.AddMilliseconds(700);
                        while (DateTime.UtcNow < until)
                        {
                            if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                            byte[] buf = new byte[1500];
                            EndPoint from = new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                            int n = s.ReceiveFrom(buf, ref from);
                            var r = ParseStun(buf, n, tx);
                            if (r != null) return r;
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        static IPEndPoint ParseStun(byte[] b, int n, byte[] tx)
        {
            if (n < 20 || b[0] != 0x01 || b[1] != 0x01) return null; // Binding Success
            for (int i = 0; i < 12; i++) if (b[8 + i] != tx[i]) return null;
            int end = Math.Min(n, 20 + ((b[2] << 8) | b[3]));
            int pos = 20;
            IPEndPoint mapped = null;
            while (pos + 4 <= end)
            {
                int type = (b[pos] << 8) | b[pos + 1];
                int len = (b[pos + 2] << 8) | b[pos + 3];
                int v = pos + 4;
                if (v + len > end) break;
                if ((type == 0x0020 || type == 0x0001) && len >= 8)
                {
                    int fam = b[v + 1];
                    int port = (b[v + 2] << 8) | b[v + 3];
                    byte[] ip = new byte[fam == 1 ? 4 : 16];
                    if (v + 4 + ip.Length > end) break;
                    Buffer.BlockCopy(b, v + 4, ip, 0, ip.Length);
                    if (type == 0x0020)
                    {
                        // XOR-MAPPED-ADDRESS：以 magic cookie（+ transaction id）還原
                        port ^= 0x2112;
                        for (int i = 0; i < ip.Length; i++) ip[i] ^= i < 4 ? b[4 + i] : tx[i - 4];
                        return new IPEndPoint(new IPAddress(ip), port);
                    }
                    mapped = new IPEndPoint(new IPAddress(ip), port);
                }
                pos = v + ((len + 3) & ~3);
            }
            return mapped;
        }
    }

    // 與單一對象之間的 P2P 通道：UDP 打洞 + 可靠傳輸 + 轉送一條 TCP 連線
    public sealed class Peer
    {
        const byte PUNCH = 1, PUNCH_ACK = 2, DATA = 3, ACK = 4, PING = 5, PONG = 6, OPEN = 7, OPEN_OK = 8, REJECT = 9;
        // [type][stream][session x8][seq/ack x4] payload [HMAC x16]
        const int HDR = 14, TAG = 16, MAXDATA = 1100, WINDOW = 256;

        sealed class Pending
        {
            public byte[] Pkt;
            public DateTime Sent;
            public int Tries;
        }

        public readonly string Name;
        readonly Keys keys;
        readonly Socket sock;
        readonly bool isHost;
        readonly int tcpPort;
        readonly byte[] sess;
        readonly object lk = new object();
        readonly List<IPEndPoint> cands = new List<IPEndPoint>();

        IPEndPoint remote;
        bool connected, everConnected, punchWarned, rttShown;
        volatile bool closed;
        DateTime created, lastRecv, lastPing, lastPunch, punchStart;
        double srtt = -1, rttvar, rto = 300;

        byte stream;
        bool streamOpen, openPending;
        DateTime openSent, openStart;
        TcpClient tcp;
        NetworkStream ns;
        // 寫入本機 TCP 由專屬執行緒處理，避免持有 lk 時被阻塞造成死結
        System.Collections.Concurrent.BlockingCollection<byte[]> outQ;
        uint sendSeq, recvNext, lastAck;
        int dupAcks;
        readonly Dictionary<uint, Pending> unacked = new Dictionary<uint, Pending>();
        readonly Dictionary<uint, byte[]> ooo = new Dictionary<uint, byte[]>();

        public Peer(string name, Keys k, Socket s, bool host, int port, byte[] session)
        {
            Name = name;
            keys = k;
            sock = s;
            isHost = host;
            tcpPort = port;
            sess = session;
            sock.ReceiveTimeout = 500;
            created = punchStart = DateTime.UtcNow;
        }

        public bool Connected { get { lock (lk) return connected; } }

        // 從未連上且超過 60 秒，或斷線超過 60 秒
        public bool Stale
        {
            get { lock (lk) return !connected && (DateTime.UtcNow - (everConnected ? lastRecv : created)).TotalSeconds > 60; }
        }

        public void AddCandidates(string list)
        {
            lock (lk)
            {
                foreach (var ep in Net.Split(list))
                {
                    if (sock.AddressFamily == AddressFamily.InterNetwork && ep.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!cands.Contains(ep)) cands.Add(ep);
                }
            }
        }

        public void Start()
        {
            var r = new Thread(RecvLoop);
            r.IsBackground = true;
            r.Start();
            var t = new Thread(TickLoop);
            t.IsBackground = true;
            t.Start();
        }

        public void Close()
        {
            closed = true;
            lock (lk) CloseStream();
            try { sock.Close(); } catch (Exception) { }
        }

        static uint U32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        static int Diff(uint a, uint b)
        {
            return (int)(a - b);
        }

        byte[] Build(byte type, byte sid, uint num, byte[] payload, int len)
        {
            byte[] p = new byte[HDR + len + TAG];
            p[0] = type;
            p[1] = sid;
            Buffer.BlockCopy(sess, 0, p, 2, 8);
            p[10] = (byte)(num >> 24); p[11] = (byte)(num >> 16); p[12] = (byte)(num >> 8); p[13] = (byte)num;
            if (len > 0) Buffer.BlockCopy(payload, 0, p, HDR, len);
            Buffer.BlockCopy(keys.Mac(p, 0, HDR + len), 0, p, HDR + len, TAG);
            return p;
        }

        byte[] Ctrl(byte type, byte sid, uint num)
        {
            return Build(type, sid, num, null, 0);
        }

        // 測試用：VBAMP2P_LOSS=0.2 會隨機丟掉 20% 送出的封包
        static readonly double SimLoss = ParseLoss();
        static readonly Random LossRng = new Random();

        static double ParseLoss()
        {
            double v;
            return double.TryParse(Environment.GetEnvironmentVariable("VBAMP2P_LOSS"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        void SendRaw(byte[] p, IPEndPoint ep)
        {
            if (ep == null) return;
            if (SimLoss > 0) lock (LossRng) if (LossRng.NextDouble() < SimLoss) return;
            try { sock.SendTo(p, Net.Target(sock, ep)); } catch (Exception) { }
        }

        void SendRaw(byte[] p)
        {
            SendRaw(p, remote);
        }

        static string Describe(IPEndPoint ep)
        {
            if (ep.AddressFamily == AddressFamily.InterNetworkV6) return "IPv6 直連 " + ep;
            byte[] a = ep.Address.GetAddressBytes();
            bool lan = a[0] == 10 || (a[0] == 172 && a[1] >= 16 && a[1] < 32) || (a[0] == 192 && a[1] == 168);
            return (lan ? "區域網路 " : "IPv4 打洞 ") + ep;
        }

        void RecvLoop()
        {
            byte[] buf = new byte[2048];
            while (!closed)
            {
                EndPoint from = new IPEndPoint(sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                int n;
                try { n = sock.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }
                if (n < HDR + TAG) continue;
                bool same = true;
                for (int i = 0; i < 8; i++) if (buf[2 + i] != sess[i]) { same = false; break; }
                if (!same || !keys.VerifyMac(buf, n, TAG)) continue;
                try { Handle(buf, n - TAG, Net.Norm((IPEndPoint)from)); }
                catch (Exception ex) { Log.W("[{0}] 處理封包錯誤：{1}", Name, ex.Message); }
            }
        }

        void Handle(byte[] b, int n, IPEndPoint ep)
        {
            byte type = b[0], sid = b[1];
            uint num = U32(b, 10);
            lock (lk)
            {
                lastRecv = DateTime.UtcNow;
                if (!connected)
                {
                    connected = everConnected = true;
                    remote = ep;
                    lastPing = DateTime.MinValue;
                    rttShown = false;
                    Log.W("[{0}] P2P 已打通：{1}", Name, Describe(ep));
                }
                else if (type != PUNCH && type != PUNCH_ACK && !ep.Equals(remote))
                {
                    remote = ep; // 對方 NAT 對應改變，跟隨已驗證的新位址
                }

                switch (type)
                {
                    case PUNCH: SendRaw(Ctrl(PUNCH_ACK, 0, 0), ep); break;
                    case PING: SendRaw(Ctrl(PONG, 0, num)); break;
                    case PONG: OnRtt((uint)Environment.TickCount - num); break;
                    case OPEN: if (isHost) HostOpen(sid); break;
                    case OPEN_OK:
                        if (!isHost && sid == stream && openPending)
                        {
                            openPending = false;
                            Log.W("[{0}] 遊戲連線已建立", Name);
                        }
                        break;
                    case REJECT:
                        if (!isHost && sid == stream && streamOpen)
                        {
                            Log.W("[{0}] 主機的 VBA-M 還沒開始等待連線，請主機先在「開始網路連線」選伺服器並按開始，再重試", Name);
                            CloseStream();
                        }
                        break;
                    case DATA: OnData(sid, num, b, n - HDR); break;
                    case ACK: OnAck(sid, num); break;
                }
            }
        }

        void OnRtt(uint ms)
        {
            if (ms > 10000) return;
            if (srtt < 0) { srtt = ms; rttvar = ms / 2.0; }
            else
            {
                rttvar = 0.75 * rttvar + 0.25 * Math.Abs(srtt - ms);
                srtt = 0.875 * srtt + 0.125 * ms;
            }
            rto = Math.Max(40, Math.Min(1500, srtt + 4 * rttvar));
            if (!rttShown)
            {
                rttShown = true;
                Log.W("[{0}] 延遲 {1} ms", Name, ms);
            }
        }

        void HostOpen(byte sid)
        {
            if (streamOpen && sid == stream)
            {
                SendRaw(Ctrl(OPEN_OK, sid, 0));
                return;
            }
            CloseStream();
            var c = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                c.NoDelay = true;
                c.Connect(IPAddress.Loopback, tcpPort);
            }
            catch (Exception)
            {
                c.Close();
                SendRaw(Ctrl(REJECT, sid, 0));
                Log.W("[{0}] 對方要求遊戲連線，但本機 VBA-M 還沒開始等待（請在「開始網路連線」選伺服器並按開始）", Name);
                return;
            }
            BeginStream(sid, c);
            SendRaw(Ctrl(OPEN_OK, sid, 0));
            Log.W("[{0}] 遊戲連線已建立", Name);
        }

        public void GuestOpen(TcpClient c)
        {
            lock (lk)
            {
                if (!connected)
                {
                    c.Close();
                    Log.W("還沒和主機打通 P2P，請等出現「P2P 已打通」後再按連線");
                    return;
                }
                CloseStream();
                c.NoDelay = true;
                BeginStream((byte)(stream + 1), c);
                openPending = true;
                openStart = openSent = DateTime.UtcNow;
                SendRaw(Ctrl(OPEN, stream, 0));
                Log.W("[{0}] VBA-M 要求連線，通知主機中...", Name);
            }
        }

        void BeginStream(byte sid, TcpClient c)
        {
            stream = sid;
            tcp = c;
            ns = c.GetStream();
            sendSeq = recvNext = lastAck = 0;
            dupAcks = 0;
            unacked.Clear();
            ooo.Clear();
            streamOpen = true;
            var s = ns;
            var q = outQ = new System.Collections.Concurrent.BlockingCollection<byte[]>();
            var t = new Thread(() => TcpReader(sid, s));
            t.IsBackground = true;
            t.Start();
            var w = new Thread(() => TcpWriter(s, q));
            w.IsBackground = true;
            w.Start();
        }

        static void TcpWriter(NetworkStream s, System.Collections.Concurrent.BlockingCollection<byte[]> q)
        {
            try
            {
                foreach (var data in q.GetConsumingEnumerable()) s.Write(data, 0, data.Length);
            }
            catch (Exception) { }
            try { s.Close(); } catch (Exception) { }
        }

        // flush = true：讓寫入執行緒送完佇列中的資料後再關閉連線（收到對方 FIN 時）
        void CloseStream(bool flush = false)
        {
            if (outQ != null)
            {
                outQ.CompleteAdding();
                outQ = null;
            }
            if (tcp != null)
            {
                if (!flush) try { tcp.Close(); } catch (Exception) { }
                tcp = null;
                ns = null;
            }
            streamOpen = openPending = false;
            unacked.Clear();
            ooo.Clear();
            Monitor.PulseAll(lk);
        }

        void TcpReader(byte sid, NetworkStream s)
        {
            byte[] buf = new byte[MAXDATA];
            while (true)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch (Exception ex) { Log.D("[{0}] tcp read error: {1}", Name, ex.Message); n = 0; }
                Log.D("[{0}] tcp read {1} bytes (stream {2})", Name, n, sid);
                lock (lk)
                {
                    while (streamOpen && stream == sid && unacked.Count >= WINDOW && !closed) Monitor.Wait(lk, 100);
                    if (!streamOpen || stream != sid) return;
                    // n == 0 代表本機 VBA-M 關閉連線：送出長度 0 的 DATA 當作 FIN
                    uint seq = sendSeq++;
                    byte[] pkt = Build(DATA, sid, seq, buf, n);
                    var p = new Pending();
                    p.Pkt = pkt;
                    p.Sent = DateTime.UtcNow;
                    p.Tries = 1;
                    unacked[seq] = p;
                    SendRaw(pkt);
                    if (n == 0) return;
                }
            }
        }

        void OnData(byte sid, uint seq, byte[] b, int len)
        {
            Log.D("[{0}] DATA sid={1} seq={2} len={3} (open={4} stream={5} next={6})", Name, sid, seq, len, streamOpen, stream, recvNext);
            if (!streamOpen || sid != stream) return;
            int d = Diff(seq, recvNext);
            if (d >= 0 && d < 8192 && !ooo.ContainsKey(seq))
            {
                byte[] data = new byte[len];
                Buffer.BlockCopy(b, HDR, data, 0, len);
                ooo[seq] = data;
            }
            byte[] next;
            bool fin = false;
            while (ooo.TryGetValue(recvNext, out next))
            {
                ooo.Remove(recvNext);
                recvNext++;
                if (next.Length == 0) { fin = true; break; }
                outQ.Add(next);
            }
            SendRaw(Ctrl(ACK, sid, recvNext));
            if (fin)
            {
                Log.W("[{0}] 對方的 VBA-M 已中斷遊戲連線", Name);
                CloseStream(true);
            }
        }

        void OnAck(byte sid, uint ack)
        {
            Log.D("[{0}] ACK sid={1} ack={2} unacked={3}", Name, sid, ack, unacked.Count);
            if (!streamOpen || sid != stream) return;
            var done = new List<uint>();
            foreach (var k in unacked.Keys) if (Diff(k, ack) < 0) done.Add(k);
            foreach (var k in done) unacked.Remove(k);
            if (done.Count > 0)
            {
                lastAck = ack;
                dupAcks = 0;
                Monitor.PulseAll(lk);
            }
            else if (ack == lastAck && ++dupAcks == 2)
            {
                Pending p; // 快速重傳
                if (unacked.TryGetValue(ack, out p))
                {
                    SendRaw(p.Pkt);
                    p.Sent = DateTime.UtcNow;
                }
            }
        }

        void TickLoop()
        {
            while (!closed)
            {
                Thread.Sleep(10);
                lock (lk)
                {
                    DateTime now = DateTime.UtcNow;
                    if (!connected)
                    {
                        if ((now - lastPunch).TotalMilliseconds >= 200 && cands.Count > 0)
                        {
                            lastPunch = now;
                            byte[] p = Ctrl(PUNCH, 0, 0);
                            foreach (var c in cands) SendRaw(p, c);
                        }
                        if (!punchWarned && (now - punchStart).TotalSeconds > 30)
                        {
                            punchWarned = true;
                            Log.W("[{0}] 30 秒內無法打通 P2P。可能雙方都在對稱型 NAT 後面（例如行動網路、部分電信 CGNAT），或防火牆擋住了 UDP", Name);
                        }
                        continue;
                    }
                    if ((now - lastRecv).TotalSeconds > 15)
                    {
                        connected = false;
                        punchStart = now;
                        punchWarned = false;
                        CloseStream();
                        Log.W("[{0}] 與對方失去聯繫，重新嘗試連線...", Name);
                        continue;
                    }
                    if ((now - lastPing).TotalMilliseconds >= 1000)
                    {
                        lastPing = now;
                        SendRaw(Ctrl(PING, 0, (uint)Environment.TickCount));
                    }
                    foreach (var p in unacked.Values)
                    {
                        if ((now - p.Sent).TotalMilliseconds >= rto * Math.Min(p.Tries, 8))
                        {
                            SendRaw(p.Pkt);
                            p.Sent = now;
                            p.Tries++;
                        }
                    }
                    if (openPending && (now - openSent).TotalMilliseconds >= Math.Max(rto, 200))
                    {
                        openSent = now;
                        SendRaw(Ctrl(OPEN, stream, 0));
                        if ((now - openStart).TotalSeconds > 10)
                        {
                            Log.W("[{0}] 主機沒有回應遊戲連線要求", Name);
                            CloseStream();
                        }
                    }
                }
            }
        }
    }

    public static class Runner
    {
        static Dictionary<string, string> Msg(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        static string Get(Dictionary<string, string> m, string k)
        {
            string v;
            return m.TryGetValue(k, out v) ? v : null;
        }

        static void Init()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
        }

        public static void RunHost(string code, int port, int maxGuests)
        {
            Init();
            var keys = new Keys(code);
            var sig = new Signal(keys);
            string myId = BitConverter.ToString(Keys.Random(6)).Replace("-", "");
            var peers = new Dictionary<string, Peer>();
            var offers = new Dictionary<string, Dictionary<string, string>>();
            int nextPlayer = 2;
            object lk = new object();

            sig.OnMessage = m =>
            {
                string from = Get(m, "from");
                if (Get(m, "t") != "hello" || from == null) return;
                lock (lk)
                {
                    foreach (var id in new List<string>(peers.Keys))
                    {
                        if (!peers[id].Stale) continue;
                        Log.W("[{0}] 已離線，釋放名額", peers[id].Name);
                        peers[id].Close();
                        peers.Remove(id);
                        offers.Remove(id);
                    }
                    Peer p;
                    if (!peers.TryGetValue(from, out p))
                    {
                        if (peers.Count >= maxGuests)
                        {
                            Log.W("已達 {0} 人上限，忽略新的加入要求", maxGuests + 1);
                            return;
                        }
                        bool v6;
                        Socket s = Net.NewUdp(out v6);
                        var cands = Net.Gather(s, v6);
                        byte[] sess = Keys.Random(8);
                        p = new Peer("玩家" + nextPlayer++, keys, s, true, port, sess);
                        peers[from] = p;
                        offers[from] = Msg("t", "offer", "from", myId, "to", from, "sess", Convert.ToBase64String(sess), "cands", Net.Join(cands));
                        Log.W("[{0}] 有人加入，開始建立 P2P 連線...", p.Name);
                        p.Start();
                    }
                    p.AddCandidates(Get(m, "cands"));
                    if (!p.Connected) sig.Send(offers[from]);
                }
            };

            sig.Start();
            Log.W("房間已建立，等待其他玩家用相同配對碼加入...");
            Thread.Sleep(Timeout.Infinite);
        }

        public static void RunGuest(string code, int port)
        {
            Init();
            var keys = new Keys(code);
            var sig = new Signal(keys);
            string myId = BitConverter.ToString(Keys.Random(6)).Replace("-", "");
            Peer peer = null;
            object lk = new object();

            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); }
            catch (SocketException)
            {
                Log.W("錯誤：本機連接埠 {0} 已被占用（是否已開著另一個連線視窗？）", port);
                return;
            }

            bool v6;
            Socket s = Net.NewUdp(out v6);
            var cands = Net.Gather(s, v6);

            sig.OnMessage = m =>
            {
                if (Get(m, "t") != "offer" || Get(m, "to") != myId) return;
                lock (lk)
                {
                    if (peer == null)
                    {
                        byte[] sess;
                        try { sess = Convert.FromBase64String(Get(m, "sess")); } catch (Exception) { return; }
                        if (sess.Length != 8) return;
                        peer = new Peer("主機", keys, s, false, port, sess);
                        peer.AddCandidates(Get(m, "cands"));
                        peer.Start();
                        Log.W("找到主機，開始建立 P2P 連線...");
                    }
                    else peer.AddCandidates(Get(m, "cands"));
                }
            };
            sig.Start();

            var hello = new Thread(() =>
            {
                DateTime start = DateTime.UtcNow;
                bool warned = false;
                while (true)
                {
                    Peer p;
                    lock (lk) p = peer;
                    if (p == null || !p.Connected)
                        sig.Send(Msg("t", "hello", "from", myId, "cands", Net.Join(cands)));
                    if (p == null && !warned && (DateTime.UtcNow - start).TotalSeconds > 30)
                    {
                        warned = true;
                        Log.W("30 秒內沒有找到主機，請確認主機已建立房間、配對碼相同");
                    }
                    Thread.Sleep(5000);
                }
            });
            hello.IsBackground = true;
            hello.Start();

            Log.W("尋找使用相同配對碼的主機...");
            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                Peer p;
                lock (lk) p = peer;
                if (p == null)
                {
                    c.Close();
                    Log.W("還沒找到主機，請等出現「P2P 已打通」後再按連線");
                    continue;
                }
                p.GuestOpen(c);
            }
        }
    }
}
