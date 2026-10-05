using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateChat;

internal static class TextDocumentTests
{
    public static async Task<int> Run()
    {
        string root = Path.Combine(RuntimeSecurity.Base, "qa", "text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string report = Path.Combine(RuntimeSecurity.Base, "text-file-qa.json");
        var checks = new List<string>();
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        try
        {
            string body = "SYNTHETIC-TEXT-" + Guid.NewGuid().ToString("N") + " 中文\r\nTab\t🙂\n姓名与地址作为正文保留";
            byte[] utf8 = new UTF8Encoding(false, true).GetBytes(body);
            var variants = new[]
            {
                (Name: "utf8", Bytes: utf8),
                (Name: "utf8-bom", Bytes: new byte[]{0xef,0xbb,0xbf}.Concat(utf8).ToArray()),
                (Name: "utf16-le", Bytes: Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(body)).ToArray()),
                (Name: "utf16-be", Bytes: Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(body)).ToArray()),
            };
            foreach (var variant in variants)
            {
                byte[] normalized = TextDocument.Normalize(variant.Bytes);
                Assert(normalized.SequenceEqual(utf8), "Normalization preserves Unicode, tabs and line endings: " + variant.Name);
                Assert(TextDocument.Normalize(normalized).SequenceEqual(normalized), "Accepted normalization is idempotent: " + variant.Name);
            }
            byte[] innerMarker = Encoding.UTF8.GetBytes("body\uFEFFinside");
            Assert(TextDocument.Normalize(TextDocument.Normalize(innerMarker)).SequenceEqual(innerMarker), "A Unicode marker within the body is preserved across passes");
            Assert(TextDocument.Normalize([]).Length == 0 && TextDocument.Normalize(new byte[]{0xef,0xbb,0xbf}).Length == 0, "Empty and BOM-only text produce an empty UTF-8 document");
            Assert(TextDocument.HasTextExtension("sample.TXT") && !TextDocument.HasTextExtension("sample.txt.exe"), "Text suffix is case-insensitive and rejects a double-extension executable");
            string longName = new string('文',200) + ".txt";
            string boundedName = FileTransfer.SafeName(longName);
            Assert(boundedName.Length<=180 && TextDocument.HasTextExtension(boundedName), "Long text filename retains its extension when bounded for transfer");
            foreach (string name in new[]{"photo.jpg","report.docx","report.pdf","archive.zip","no-extension"})
                Assert(Reject(()=>TextDocument.RequireTextExtension(name)), "Non-text file extension rejected: " + name);
            var invalid = new[]
            {
                (Name:"PNG disguised as TXT", Bytes:new byte[]{137,80,78,71,13,10,26,10}),
                (Name:"ZIP disguised as TXT", Bytes:new byte[]{80,75,3,4,20,0}),
                (Name:"NUL inside UTF-8", Bytes:Encoding.UTF8.GetBytes("hello\0world")),
                (Name:"Invalid UTF-8", Bytes:new byte[]{0xc3,0x28}),
                (Name:"Truncated UTF-16", Bytes:new byte[]{0xff,0xfe,0x61}),
                (Name:"Legacy encoding without BOM", Bytes:new byte[]{0xd6,0xd0,0xce,0xc4}),
                (Name:"UTF-32", Bytes:new byte[]{0xff,0xfe,0,0,0x61,0,0,0}),
                (Name:"PDF disguised as TXT", Bytes:Encoding.UTF8.GetBytes("%PDF-1.4\n1 0 obj")),
                (Name:"RTF disguised as TXT", Bytes:Encoding.UTF8.GetBytes("{\\rtf1\\ansi hidden}")),
                (Name:"Repeated UTF-8 BOM", Bytes:Encoding.UTF8.GetBytes("\uFEFF\uFEFFhello")),
                (Name:"Three UTF-8 BOMs", Bytes:Encoding.UTF8.GetBytes("\uFEFF\uFEFF\uFEFFhello")),
                (Name:"PDF behind repeated BOMs", Bytes:Encoding.UTF8.GetBytes("\uFEFF\uFEFF%PDF-1.7")),
                (Name:"Repeated UTF-16 LE BOM", Bytes:Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("\uFEFFhello")).ToArray()),
                (Name:"Repeated UTF-16 BE BOM", Bytes:Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("\uFEFFhello")).ToArray()),
            };
            foreach (var variant in invalid) Assert(Reject(()=>TextDocument.Normalize(variant.Bytes)), "Invalid text rejected: " + variant.Name);
            byte[] oversized = new byte[checked((int)FileTransfer.MaximumBytes + 1)];
            Assert(Reject(()=>TextDocument.Normalize(oversized)), "Input byte limit enforced before decoding");
            string expansion = new('中', checked((int)(FileTransfer.MaximumBytes / 3 + 1)));
            byte[] utf16Expansion = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(expansion)).ToArray();
            Assert(utf16Expansion.Length < FileTransfer.MaximumBytes && Reject(()=>TextDocument.Normalize(utf16Expansion)), "UTF-16 conversion cannot exceed the output byte limit");

            string profile = Path.Combine(root,"profile"); Directory.CreateDirectory(profile);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var client = new CoreClient();
            await client.Init(Path.Combine(profile,"chat"), password);
            await client.Result("/_create user {\"profile\":null,\"pastTimestamp\":false}");
            await FileTransfer.Configure(client,profile);
            foreach (var variant in variants)
            {
                string original = Path.Combine(root,variant.Name+".TXT"); File.WriteAllBytes(original,variant.Bytes);
                File.WriteAllText(original+":private-author", "SYNTHETIC-AUTHOR-GPS-ATTRIBUTE");
                byte[] before = SHA256.HashData(File.ReadAllBytes(original));
                var source = await client.EncryptFile(original);
                string output = Path.Combine(root,variant.Name+"-export.txt");
                await client.ExportFile(source,utf8.Length,output);
                Assert(File.ReadAllBytes(output).SequenceEqual(utf8), "Release worker stages ciphertext and exports normalized bytes: " + variant.Name);
                Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))) && File.ReadAllText(original+":private-author")=="SYNTHETIC-AUTHOR-GPS-ATTRIBUTE", "Original bytes and extra attributes unchanged: " + variant.Name);
                Assert(!File.Exists(output+":private-author") && !File.ReadAllText(output).Contains("SYNTHETIC-AUTHOR-GPS-ATTRIBUTE"), "Original alternate data stream is not transmitted: " + variant.Name);
                Assert(File.ReadAllText(output+":Zone.Identifier").Contains("ZoneId=3"), "Normalized export retains Windows Internet-zone marker: " + variant.Name);
                string invalidTarget = Path.Combine(root,variant.Name+"-export.html");
                Assert(await RejectAsync(()=>client.ExportFile(source,utf8.Length,invalidTarget)) && !File.Exists(invalidTarget), "Worker refuses an executable/document export extension: " + variant.Name);
            }
            int beforeCount = Directory.GetFiles(FileTransfer.Cache(profile)).Length;
            string nonText = Path.Combine(root,"text-in-wrong-format.pdf"); File.WriteAllBytes(nonText,utf8);
            Assert(await RejectAsync(()=>client.EncryptFile(nonText)), "Worker rejects a non-TXT extension even when contents are text");
            foreach (var (variant,index) in invalid.Select((v,i)=>(v,i)))
            {
                string file = Path.Combine(root,"invalid-"+index+".txt"); File.WriteAllBytes(file,variant.Bytes);
                Assert(await RejectAsync(()=>client.EncryptFile(file)), "Worker rejects disguised/invalid text independently of UI: " + variant.Name);
            }
            Assert(Directory.GetFiles(FileTransfer.Cache(profile)).Length==beforeCount, "Rejected uploads leave no staged files");
            Assert(Reject(()=>FileTransfer.SendCommand(1,new JsonObject(),"fake.pdf")), "File command builder also refuses a non-TXT display name");
            bool rejectsOriginal = false;
            try { FileTransfer.SendCommand(1,new JsonObject(),"SYNTHETIC-PRIVATE-AUTHOR.txt"); } catch(IOException) { rejectsOriginal = true; }
            Assert(rejectsOriginal, "File command builder refuses an original filename even with a TXT suffix");
            string privateName = "SYNTHETIC-PRIVATE-AUTHOR-LOCATION.txt";
            string privatePath = Path.Combine(root,privateName); File.WriteAllBytes(privatePath,utf8);
            var privateSource = await client.EncryptFile(privatePath);
            string firstName = FileTransfer.NewTextName(), secondName = FileTransfer.NewTextName();
            Assert(System.Text.RegularExpressions.Regex.IsMatch(firstName,@"\A文档-[0-9a-f]{24}\.txt\z") && firstName != secondName, "Each send gets a fresh random text label");
            string send = FileTransfer.SendCommand(1,privateSource,firstName);
            var payload = JsonNode.Parse(send[(send.IndexOf(" json ",StringComparison.Ordinal)+6)..])!;
            Assert(payload[0]!["msgContent"]!["text"]!.ToString()==firstName && !payload.ToJsonString().Contains(privateName) && !payload.ToJsonString().Contains(root), "Send payload carries the confirmed anonymous label and no original name or path");
            Assert(File.Exists(privatePath) && File.ReadAllBytes(privatePath).SequenceEqual(utf8) && !File.Exists(Path.Combine(root,firstName)), "Anonymous naming never renames or changes the source document");
            SecurityTests.ScanSyntheticFiles(profile,new[]{body,password,"SYNTHETIC-AUTHOR-GPS-ATTRIBUTE"},Assert);
            File.WriteAllText(report,new JsonObject{["status"]="passed",["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray()),["syntheticRoot"]=root}.ToJsonString(new(){WriteIndented=true}));
            return 0;
        }
        catch(Exception ex)
        {
            File.WriteAllText(report,new JsonObject{["status"]="incomplete",["reason"]=ex.Message,["checks"]=new JsonArray(checks.Select(c=>JsonValue.Create(c)).ToArray()),["syntheticRoot"]=root}.ToJsonString(new(){WriteIndented=true}));
            Console.WriteLine("INCOMPLETE " + ex.Message); return 1;
        }
    }
    private static bool Reject(Action action) { try { action(); return false; } catch(TextDocumentException) { return true; } }
    private static async Task<bool> RejectAsync(Func<Task<JsonNode>> action) { try { await action(); return false; } catch(TextDocumentException) { return true; } }
}
