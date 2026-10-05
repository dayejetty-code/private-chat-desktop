using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class TorRouteTests
{
    private static readonly string A=new('A',40), B=new('B',40), C=new('C',40);
    private static string Circuit => $"21 BUILT ${A}~Entry,${B}~Middle,${C}~Last PURPOSE=GENERAL SOCKS_USERNAME=\"SYNTHETIC-SECRET\" SOCKS_PASSWORD=\"SYNTHETIC-SECRET\"";
    public static async Task<int> Run()
    {
        var checks=new List<string>();
        void Assert(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
        try
        {
            Assert(TorRoutes.ReadPort("PORT=127.0.0.1:12345\r\n")==12345,"Control endpoint accepts only a local IPv4 port file");
            foreach(string invalid in new[]{"PORT=0.0.0.0:12345","PORT=192.0.2.1:12345","PORT=127.0.0.1:0","PORT=127.0.0.1:65536","PORT=127.0.0.1:12345\nGETINFO secret"})
                Assert(Reject(()=>TorRoutes.ReadPort(invalid)),"Nonlocal, out-of-range or injected control endpoint rejected");
            var model=TorRoutes.Parse(new[]{Circuit,$"22 BUILT ${C}~Last PURPOSE=HS_CLIENT_REND","23 LAUNCHED PURPOSE=GENERAL"},new[]{"1 SUCCEEDED 21 relay.example:5223","2 SUCCEEDED 21 relay.example:5223","3 NEW 0 pending.example:443","4 SUCCEEDED 22 service.onion:443"},DateTimeOffset.UtcNow);
            Assert(model.Circuits[0].Hops.Select(h=>h.Fingerprint).SequenceEqual(new[]{A,B,C}),"Circuit parser preserves exact Tor node order and fingerprints");
            Assert(model.Circuits[0].Connections==2 && model.Circuits[0].Targets.Single()=="relay.example:5223" && model.UnassignedConnections==1,"Stream association keeps circuit IDs and unassigned connections separate");
            Assert(model.Circuits.Single(c=>c.Id=="22").Purpose=="HS_CLIENT_REND" && model.Circuits.Single(c=>c.Id=="23").Hops.Length==0,"Onion rendezvous purpose and unbuilt circuits are preserved without invented nodes");
            Assert(!JsonSerializer.Serialize(model).Contains("SYNTHETIC-SECRET"),"Snapshot excludes SOCKS credentials and raw control metadata");
            Assert(Reject(()=>TorRoutes.Parse(new[]{"1 BUILT $bad~INJECT PURPOSE=GENERAL"},Array.Empty<string>(),DateTimeOffset.UtcNow)),"Invalid node fingerprint cannot become a descriptor query");
            var capped=TorRoutes.Parse(Enumerable.Range(1,70).Select(i=>$"{i} BUILT ${A}~Entry PURPOSE=GENERAL").ToArray(),new[]{"1 SUCCEEDED 70 relay.example:443"},DateTimeOffset.UtcNow);
            Assert(capped.Circuits.Length==64 && capped.TotalCircuits==70 && capped.Circuits[0].Id=="70","Large snapshots are bounded and prioritize associated circuits with an honest total");
            using(var wire=new TorRoutes.ControlWire(new MemoryStream(Encoding.ASCII.GetBytes("250+sample=\r\n..dot\r\n.\r\n250 OK\r\n"))))
                Assert((await wire.Reply(CancellationToken.None)).Data("sample").Single()==".dot","Multiline control replies unescape dot-stuffed data");
            using(var wire=new TorRoutes.ControlWire(new MemoryStream(Encoding.ASCII.GetBytes(new string('x',TorRoutes.MaximumLine+1)+"\r\n"))))
                Assert(await RejectAsync(()=>wire.Reply(CancellationToken.None)),"Oversized control line rejected before unbounded allocation");
            using(var wire=new TorRoutes.ControlWire(new MemoryStream(Encoding.ASCII.GetBytes("250+sample=\r\n"+string.Concat(Enumerable.Repeat(new string('x',1000)+"\r\n",600))+".\r\n250 OK\r\n"))))
                Assert(await RejectAsync(()=>wire.Reply(CancellationToken.None)),"Oversized multiline reply rejected");
            using(var wire=new TorRoutes.ControlWire(new MemoryStream(Encoding.ASCII.GetBytes("250 sample\n"))))
                Assert(await RejectAsync(()=>wire.Reply(CancellationToken.None)),"Malformed control line ending rejected");
            var normal=await Fake("normal");
            Assert(normal.Snapshot?.Circuits[0].Hops[0].Address=="192.0.2.1" && normal.Snapshot.Circuits[0].Hops[0].Country=="DE","SAFECOOKIE round trip verifies both proofs and reads descriptor/GeoIP data locally");
            Assert(normal.Snapshot!.Circuits[0].Hops[1].Address==null,"Missing node directory data remains unknown");
            var untrusted=await Fake("wrong-proof");
            Assert(untrusted.Rejected && !untrusted.AuthSent,"Untrusted endpoint receives no client authentication proof");
            var wrongPid=await Fake("wrong-pid");
            Assert(wrongPid.Rejected && !wrongPid.QueriedRoutes,"Authenticated endpoint with wrong process ID receives no route query");
            var empty=await Fake("empty");
            Assert(empty.Snapshot?.Circuits.Length==0,"Tor empty GETINFO values are represented as an empty snapshot");
            Write("passed",checks);return 0;
        }
        catch(Exception ex){Write("incomplete",checks,ex.Message);Console.WriteLine("INCOMPLETE "+ex.Message);return 1;}
    }
    private static bool Reject(Action action){try{action();return false;}catch(IOException){return true;}}
    private static async Task<bool> RejectAsync(Func<Task> action){try{await action();return false;}catch(IOException){return true;}}
    private static void Write(string status,List<string> checks,string? reason=null)=>File.WriteAllText(Path.Combine(RuntimeSecurity.Base,"tor-route-qa.json"),new JsonObject{["status"]=status,["reason"]=reason,["checks"]=new JsonArray(checks.Select(s=>JsonValue.Create(s)).ToArray())}.ToJsonString(new(){WriteIndented=true}));
    private static async Task<(TorRouteSnapshot? Snapshot,bool Rejected,bool AuthSent,bool QueriedRoutes)> Fake(string mode)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] cookie=RandomNumberGenerator.GetBytes(32), nonce=RandomNumberGenerator.GetBytes(32); bool authenticated=false, queried=false;
        var server=Task.Run(async()=>
        {
            using var peer=await listener.AcceptTcpClientAsync(stop.Token); using var stream=peer.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
            using var writer=new StreamWriter(stream,Encoding.ASCII,1024,true){NewLine="\r\n",AutoFlush=true};
            string? line=await reader.ReadLineAsync(stop.Token);
            if(line==null || !line.StartsWith("AUTHCHALLENGE SAFECOOKIE ",StringComparison.Ordinal))throw new IOException("Expected safe cookie challenge");
            byte[] clientNonce=Convert.FromHexString(line[25..]);
            byte[] serverProof=mode=="wrong-proof" ? new byte[32] : TorRoutes.Proof(TorRoutes.ServerKey,cookie,clientNonce,nonce);
            await writer.WriteLineAsync("250 AUTHCHALLENGE SERVERHASH="+Convert.ToHexString(serverProof)+" SERVERNONCE="+Convert.ToHexString(nonce));
            line=await reader.ReadLineAsync(stop.Token); if(line==null)return;
            authenticated=line.StartsWith("AUTHENTICATE ",StringComparison.Ordinal);
            if(!authenticated || line[13..]!=Convert.ToHexString(TorRoutes.Proof(TorRoutes.ClientKey,cookie,clientNonce,nonce)))throw new IOException("Client proof mismatch");
            await writer.WriteLineAsync("250 OK");
            while((line=await reader.ReadLineAsync(stop.Token))!=null)
            {
                if(line=="GETINFO process/pid")await writer.WriteAsync($"250-process/pid={(mode=="wrong-pid" ? 54321 : 12345)}\r\n250 OK\r\n");
                else if(line=="GETINFO circuit-status stream-status")
                {
                    queried=true;
                    if(mode=="empty")await writer.WriteAsync("250-circuit-status=\r\n250-stream-status=\r\n250 OK\r\n");
                    else await writer.WriteAsync($"250+circuit-status=\r\n{Circuit}\r\n.\r\n250+stream-status=\r\n1 SUCCEEDED 21 relay.example:443\r\n.\r\n250 OK\r\n");
                }
                else if(line.StartsWith("GETINFO ns/id/",StringComparison.Ordinal))
                {
                    string fingerprint=line[14..];
                    if(fingerprint==B)await writer.WriteLineAsync("552 Unrecognized key");
                    else await writer.WriteAsync($"250+ns/id/{fingerprint}=\r\nr Entry id digest 2026-10-03 00:00:00 192.0.2.1 9001 0\r\n.\r\n250 OK\r\n");
                }
                else if(line=="GETINFO ip-to-country/192.0.2.1")await writer.WriteAsync("250-ip-to-country/192.0.2.1=de\r\n250 OK\r\n");
                else throw new IOException("Unexpected route command");
                await writer.FlushAsync(stop.Token);
            }
        },stop.Token);
        TorRouteSnapshot? result=null; bool rejected=false;
        try
        {
            try{result=await TorRoutes.Read(port,cookie,12345,stop.Token);}catch(IOException){rejected=true;}
            await server; return(result,rejected,authenticated,queried);
        }
        finally{listener.Stop();CryptographicOperations.ZeroMemory(cookie);CryptographicOperations.ZeroMemory(nonce);}
    }
    internal static async Task CheckLive(TorService tor,Action<bool,string> assert)
    {
        var snapshot=await tor.ReadRoutes(CancellationToken.None);
        assert(snapshot.Circuits.Any(c=>c.Connections>0 && c.Hops.Length>0),"Authenticated live Tor sample associates actual connections with actual circuit nodes");
        assert(snapshot.Circuits.SelectMany(c=>c.Hops).Any(h=>h.Address!=null),"Live node addresses are read from Tor local consensus without external lookup");
        string portPath=(string)typeof(TorService).GetField("controlPortFile",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tor)!;
        string cookiePath=(string)typeof(TorService).GetField("controlCookie",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tor)!;
        var directoryAcl=new DirectoryInfo(Path.GetDirectoryName(cookiePath)!).GetAccessControl();
        string account=WindowsIdentity.GetCurrent().User!.Value;
        var rules=new FileInfo(cookiePath).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        assert(directoryAcl.AreAccessRulesProtected && rules.Length>0 && rules.All(r=>r.IdentityReference.Value==account),"Control cookie inherits an isolated ACL allowing only the current Windows account");
        int port=TorRoutes.ReadPort(await File.ReadAllTextAsync(portPath));
        using(var client=new TcpClient(AddressFamily.InterNetwork))
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await client.ConnectAsync(IPAddress.Loopback,port,timeout.Token);
            using var wire=new TorRoutes.ControlWire(client.GetStream());
            assert((await wire.Command("GETINFO circuit-status",timeout.Token)).Code!=250,"Real Tor rejects unauthenticated route queries");
        }
        assert(await RejectAsync(()=>TorRoutes.Read(port,new byte[32],tor.ProcessId,CancellationToken.None)),"Real Tor SAFECOOKIE verification rejects an incorrect cookie");
    }
}
