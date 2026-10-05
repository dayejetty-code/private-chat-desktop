using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivateChat;

internal sealed record TorHop(string Fingerprint, string Name, string? Address = null, string? Country = null);
internal sealed record TorCircuit(string Id, string Status, string Purpose, TorHop[] Hops, string[] Targets, int Connections);
internal sealed record TorRouteSnapshot(DateTimeOffset At, TorCircuit[] Circuits, int TotalCircuits, int UnassignedConnections);

// Read-only queries against this app's Tor process. Never retain raw replies or SOCKS credentials.
internal static class TorRoutes
{
    internal static readonly string ServerKey = "Tor safe cookie authentication server-to-controller hash";
    internal static readonly string ClientKey = "Tor safe cookie authentication controller-to-server hash";
    internal const int MaximumLine = 16384, MaximumReply = 524288;
    internal static byte[] Proof(string key, byte[] cookie, byte[] clientNonce, byte[] serverNonce)
    {
        if (cookie.Length != 32 || clientNonce.Length != 32 || serverNonce.Length != 32) throw new IOException("Invalid control authentication length");
        byte[] data = new byte[96];
        try
        {
            cookie.CopyTo(data,0); clientNonce.CopyTo(data,32); serverNonce.CopyTo(data,64);
            return HMACSHA256.HashData(Encoding.ASCII.GetBytes(key),data);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    internal static int ReadPort(string text)
    {
        var match = Regex.Match(text, @"\APORT=127\.0\.0\.1:([0-9]{1,5})\r?\n?\z");
        if (!match.Success || !int.TryParse(match.Groups[1].Value,out int port) || port is < 1 or > 65535) throw new IOException("Invalid local control endpoint");
        return port;
    }
    internal static async Task<TorRouteSnapshot> Read(int port, byte[] cookie, int expectedPid, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8)); token = timeout.Token;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback,port,token);
        using var wire = new ControlWire(client.GetStream());
        byte[] nonce = RandomNumberGenerator.GetBytes(32), serverNonce = [], proof = [], expected = [], serverProof = [];
        try
        {
            var challenge = await wire.Command("AUTHCHALLENGE SAFECOOKIE " + Convert.ToHexString(nonce),token);
            if (challenge.Code != 250 || challenge.Lines.Count != 1) throw new IOException("Control authentication unavailable");
            var match = Regex.Match(challenge.Lines[0], @"\AAUTHCHALLENGE SERVERHASH=([0-9A-Fa-f]{64}) SERVERNONCE=([0-9A-Fa-f]{64})\z");
            if (!match.Success) throw new IOException("Invalid control authentication challenge");
            serverNonce = Convert.FromHexString(match.Groups[2].Value); serverProof = Convert.FromHexString(match.Groups[1].Value);
            expected = Proof(ServerKey,cookie,nonce,serverNonce);
            if (!CryptographicOperations.FixedTimeEquals(serverProof,expected)) throw new IOException("Untrusted control endpoint");
            proof = Proof(ClientKey,cookie,nonce,serverNonce);
            if ((await wire.Command("AUTHENTICATE " + Convert.ToHexString(proof),token)).Code != 250) throw new IOException("Control authentication failed");
        }
        finally
        {
            foreach (byte[] bytes in new[]{nonce,serverNonce,proof,expected,serverProof}) CryptographicOperations.ZeroMemory(bytes);
        }
        var identity = await wire.Command("GETINFO process/pid",token);
        if (identity.Code != 250 || identity.Value("process/pid") != expectedPid.ToString(CultureInfo.InvariantCulture)) throw new IOException("Unexpected Tor process");
        var reply = await wire.Command("GETINFO circuit-status stream-status",token);
        if (reply.Code != 250) throw new IOException("Tor route query failed");
        var snapshot = Parse(reply.Data("circuit-status"),reply.Data("stream-status"),DateTimeOffset.UtcNow);
        var nodes = new Dictionary<string,TorHop>(StringComparer.Ordinal);
        foreach (var node in snapshot.Circuits.SelectMany(c=>c.Hops).DistinctBy(n=>n.Fingerprint))
        {
            var descriptor = await wire.Command("GETINFO ns/id/" + node.Fingerprint,token);
            TorHop enriched = node;
            if (descriptor.Code == 250)
            {
                // The address is published consensus data, not a claim about a live socket address.
                var row = descriptor.Data("ns/id/"+node.Fingerprint).FirstOrDefault(l=>l.StartsWith("r ",StringComparison.Ordinal))?.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if (row is {Length: >= 9} && IPAddress.TryParse(row[6],out var address))
                {
                    string ip = address.ToString();
                    var location = await wire.Command("GETINFO ip-to-country/" + ip,token);
                    string? country = location.Code == 250 ? location.Value("ip-to-country/"+ip)?.ToUpperInvariant() : null;
                    if (country == null || !Regex.IsMatch(country,@"\A[A-Z]{2}\z")) country = null;
                    enriched = node with {Address=ip,Country=country};
                }
            }
            nodes[node.Fingerprint] = enriched;
        }
        token.ThrowIfCancellationRequested();
        return snapshot with {Circuits=snapshot.Circuits.Select(c=>c with {Hops=c.Hops.Select(n=>nodes[n.Fingerprint]).ToArray()}).ToArray()};
    }
    internal static TorRouteSnapshot Parse(IReadOnlyList<string> circuits, IReadOnlyList<string> streams, DateTimeOffset at)
    {
        var connections = new List<(string Circuit,string Target)>(); int unassigned = 0;
        foreach (string line in streams)
        {
            var parts = line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length<4 || !Number(parts[0]) || !Number(parts[2])) throw new IOException("Invalid stream status");
            if (parts[1] is "CLOSED" or "FAILED") continue;
            if (parts[2]=="0") { ++unassigned; continue; }
            string target = Regex.IsMatch(parts[3],@"\A[A-Za-z0-9.\-:\[\]]{1,300}\z") ? parts[3] : "目标未提供";
            connections.Add((parts[2],target));
        }
        var result = new List<TorCircuit>();
        foreach (string line in circuits)
        {
            var parts = line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length<2 || !Number(parts[0]) || !Regex.IsMatch(parts[1],@"\A[A-Z_]{1,40}\z")) throw new IOException("Invalid circuit status");
            var hops = new List<TorHop>();
            if (parts.Length>2 && parts[2].StartsWith('$'))
                foreach (string hop in parts[2].Split(','))
                {
                    var match = Regex.Match(hop,@"\A\$([A-Fa-f0-9]{40})(?:[~=]([A-Za-z0-9]{1,19}))?\z");
                    if (!match.Success || hops.Count>=16) throw new IOException("Invalid Tor path");
                    hops.Add(new TorHop(match.Groups[1].Value.ToUpperInvariant(),match.Groups[2].Success ? match.Groups[2].Value : "名称未提供"));
                }
            string purpose = parts.FirstOrDefault(p=>p.StartsWith("PURPOSE=",StringComparison.Ordinal))?[8..] ?? "UNKNOWN";
            if (!Regex.IsMatch(purpose,@"\A[A-Z0-9_]{1,64}\z")) purpose="UNKNOWN";
            var linked = connections.Where(c=>c.Circuit==parts[0]).ToArray();
            result.Add(new TorCircuit(parts[0],parts[1],purpose,hops.ToArray(),linked.Select(c=>c.Target).Distinct().Take(12).ToArray(),linked.Length));
        }
        return new TorRouteSnapshot(at,result.OrderByDescending(c=>c.Connections>0).ThenByDescending(c=>c.Status=="BUILT").Take(64).ToArray(),result.Count,unassigned);
    }
    private static bool Number(string value) => value.Length is > 0 and <= 10 && value.All(c=>c is >= '0' and <= '9');

    internal sealed record ControlReply(int Code,List<string> Lines,Dictionary<string,List<string>> Blocks)
    {
        public string? Value(string key) => Lines.FirstOrDefault(l=>l.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..];
        public IReadOnlyList<string> Data(string key) => Blocks.TryGetValue(key,out var rows) ? rows : Value(key)=="" ? Array.Empty<string>() : throw new IOException("Missing Tor reply field");
    }
    internal sealed class ControlWire(Stream stream) : IDisposable
    {
        private readonly byte[] buffer = new byte[8192];
        private int start,end;
        public async Task<ControlReply> Command(string command,CancellationToken token)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(command+"\r\n");
            try { await stream.WriteAsync(bytes,token); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return await Reply(token);
        }
        internal async Task<ControlReply> Reply(CancellationToken token)
        {
            var lines = new List<string>(); var blocks = new Dictionary<string,List<string>>(StringComparer.Ordinal); int total=0, code=0;
            async Task<string> Next()
            {
                string line = await Line(token); total += line.Length+2;
                if (total>MaximumReply) throw new IOException("Tor reply too large"); return line;
            }
            while(true)
            {
                string line = await Next();
                if (line.Length<4 || !int.TryParse(line[..3],NumberStyles.None,CultureInfo.InvariantCulture,out int current) || (code!=0 && current!=code)) throw new IOException("Invalid Tor control reply");
                code=current; char separator=line[3]; string body=line[4..];
                if (separator=='+')
                {
                    if (!body.EndsWith('=') || blocks.ContainsKey(body[..^1])) throw new IOException("Invalid Tor data reply");
                    var rows = new List<string>(); blocks[body[..^1]]=rows;
                    while(true) { string data=await Next(); if(data==".")break; rows.Add(data.StartsWith("..",StringComparison.Ordinal) ? data[1..] : data); }
                }
                else if(separator is '-' or ' ') lines.Add(body);
                else throw new IOException("Invalid Tor reply delimiter");
                if(separator==' ') return new ControlReply(code,lines,blocks);
            }
        }
        private async Task<string> Line(CancellationToken token)
        {
            using var line = new MemoryStream();
            while(true)
            {
                token.ThrowIfCancellationRequested();
                if(start==end) { start=0; end=await stream.ReadAsync(buffer,token); if(end==0)throw new IOException("Tor control connection closed"); }
                int newline=Array.IndexOf(buffer,(byte)'\n',start,end-start), stop=newline<0 ? end : newline;
                if(line.Length+stop-start>MaximumLine) throw new IOException("Tor control line too large");
                line.Write(buffer,start,stop-start); start=stop+(newline<0 ? 0 : 1);
                if(newline>=0)
                {
                    int length=(int)line.Length; byte[] data=line.GetBuffer();
                    if(length==0 || data[length-1]!='\r')throw new IOException("Invalid Tor line ending");
                    return Encoding.UTF8.GetString(data,0,length-1);
                }
            }
        }
        public void Dispose() { CryptographicOperations.ZeroMemory(buffer); stream.Dispose(); }
    }
}
