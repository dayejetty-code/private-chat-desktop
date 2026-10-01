using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class RelayTests
{
    public static void InputChecks(Action<bool,string> check)
    {
        int index=0;
        foreach (var input in new[] { "", "https://example.com", "smp://key@host\n/_stop", "smp://key@ho\tst", "smp://key@host\0", string.Join('\n',Enumerable.Repeat("smp://key@host",9)) })
        {
            bool rejected=false; try { RelaySettings.Lines(input); } catch(ArgumentException) { rejected=true; }
            check(rejected,"Relay input rejects empty, unrelated, injected or excessive list: case " + ++index);
        }
        bool duplicates=false;try { RelaySettings.Lines("smp://key@host\nsmp://key@host"); }catch(ArgumentException){duplicates=true;}
        check(duplicates,"Duplicate relay addresses rejected before testing");
        check(RelaySettings.Lines("  smp://key:password@host  \r\n\r\n").Length==1,"Relay list accepts surrounding whitespace and optional server password");
    }
    public static async Task NativeChecks(CoreClient core, long user, int port, Action<bool,string> check)
    {
        var original=await RelaySettings.Read(core,user);
        check(RelaySettings.IsPresetConfiguration(original) && RelaySettings.Enabled(original).Length>0,"Native preset SMP configuration can be read");
        var presets=RelaySettings.Enabled(original);
        await RelaySettings.Parse(core,presets[0]);
        check(true,"Native SMP parser accepts a complete pinned server address");
        bool bad=false;try {await RelaySettings.Parse(core,"smp://invalid-address");}catch(ArgumentException){bad=true;}
        check(bad,"Native parser rejects malformed SMP address before network operation");
        var strict=(await core.Result("/network"))["networkConfig"]!.DeepClone();
        try
        {
            var weakened=strict.DeepClone();weakened["smpProxyMode"]="never";
            await core.Result("/_network "+weakened.ToJsonString());
            bool blocked=false;try{await RelaySettings.Test(core,user,presets[0],port);}catch(RelayPolicyException){blocked=true;}
            check(blocked,"Relay test refuses weakened routing before opening a test connection");
        }
        finally { await core.Result("/_network "+strict.ToJsonString()); }
        var custom="smp://"+new string('A',43)+"=:SYNTHETIC-RELAY-PASSWORD@relay-qa.invalid";
        var proposed=RelaySettings.Build(original,new[]{custom});
        check(JsonNode.DeepEquals(original[0]?["xftpServers"],proposed[0]?["xftpServers"]) && JsonNode.DeepEquals(original[0]?["chatRelays"],proposed[0]?["chatRelays"]),"SMP edits preserve XFTP and chat-relay settings");
        bool canceled=false;try {await RelaySettings.Save(core,user,new[]{custom},port,()=>false);}catch(OperationCanceledException){canceled=true;}
        check(canceled && RelaySettings.Enabled(await RelaySettings.Read(core,user)).SequenceEqual(presets),"Closing settings before commit cancels save without changing active relays");
        bool invalid=false;try {await RelaySettings.Save(core,user,new[]{custom,custom.Replace("PASSWORD","OTHER-PASSWORD")},port,()=>true);}catch(ArgumentException){invalid=true;}
        check(invalid && RelaySettings.Enabled(await RelaySettings.Read(core,user)).SequenceEqual(presets),"Core rejects duplicate relay hosts without replacing current configuration");
        var saved=await RelaySettings.Save(core,user,new[]{custom},port,()=>true);
        check(RelaySettings.Enabled(saved).SequenceEqual(new[]{custom}),"Only selected custom SMP relay is enabled and read back");
        check(!RelaySettings.IsPresetConfiguration(saved),"Custom configuration distinguished from preset configuration");
        var restored=await RelaySettings.Save(core,user,null,port,()=>true);
        var allPresets=RelaySettings.Servers(original).Where(s=>s["preset"]?.GetValue<bool>()==true).Select(s=>s["server"]!.ToString()).Order(StringComparer.Ordinal);
        check(RelaySettings.IsPresetConfiguration(restored) && RelaySettings.Enabled(restored).SequenceEqual(allPresets),"Restore presets reenables bundled relays and removes custom list");
        // Leave a synthetic password-protected address in the encrypted store for the reopen/scan checks.
        await RelaySettings.Save(core,user,new[]{custom},port,()=>true);
    }
    public static async Task ReopenChecks(CoreClient core,Action<bool,string> check)
    {
        var cfg=(await core.Result("/network"))["networkConfig"]!;
        await core.Result("/_network "+PrivacyPolicy.Configure(cfg,59999).ToJsonString());
        await RelaySettings.CheckPolicy(core,59999);
        await core.Result("/_start main=on snd_files=off");
        var addresses=RelaySettings.Enabled(await RelaySettings.Read(core,1));
        check(addresses.Length==1 && addresses[0].Contains("SYNTHETIC-RELAY-PASSWORD"),"Custom relay and access password survive encrypted database reopen");
    }
    public static async Task<string[]> NetworkChecks(CoreClient core,long user,int port,Action<bool,string> check)
    {
        var original=await RelaySettings.Read(core,user);
        var selected=original.Where(g=>g?["operator"]?["enabled"]?.GetValue<bool>()==true)
            .Select(g=>g!["smpServers"]!.AsArray().First(s=>s?["enabled"]?.GetValue<bool>()==true)!["server"]!.ToString()).Take(2).ToArray();
        var tested=new List<string>();
        foreach(var address in selected)
        {
            if(await RelaySettings.Test(core,user,address,port)) tested.Add(address);
        }
        check(tested.Count>0,"Native custom-relay test succeeds through mandatory Tor");
        selected=tested.ToArray();
        var badCertificate="smp://"+new string('A',43)+"="+selected[0][selected[0].IndexOf('@')..];
        check(!await RelaySettings.Test(core,user,badCertificate,port),"Custom relay with incorrect certificate fingerprint fails its Tor connection test");
        check(RelaySettings.Enabled(await RelaySettings.Read(core,user)).SequenceEqual(RelaySettings.Enabled(original)),"A failed relay connection test does not change current configuration");
        await RelaySettings.Save(core,user,selected,port,()=>true);
        check(RelaySettings.Enabled(await RelaySettings.Read(core,user)).SequenceEqual(selected.Order(StringComparer.Ordinal)),"Explicit relay selection is active before real invitation and delivery");
        return selected;
    }
}
