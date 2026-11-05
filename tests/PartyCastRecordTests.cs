using System;
using System.IO;
using System.Linq;
using System.Text;
using SoundcoreDesk;

static class PartyCastRecordTests
{
    static int checks;
    static void Assert(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
    static void Checksum(byte[] frame)
    { frame[frame.Length - 1] = (byte)(frame.Take(frame.Length - 1).Sum(b => (int)b) & 255); }
    public static int Run()
    {
        // Captured secondary state, with a printable anonymous serial and new checksum.
        string hex = "09ff00000101013b000705010001010230322e3130544553545345434f4e44415259303031013101000013ffff022828281e141e1e1e1400020079";
        byte[] frame = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        Array.Copy(Encoding.ASCII.GetBytes("TESTSECONDARY001"), 0, frame, 21, 16); Checksum(frame);
        GlowDeviceReport state;
        Assert(SoundcoreProtocol.TryReadDeviceState(frame, out state) && state.Firmware == "02.10" && state.SerialNumber == "TESTSECONDARY001" && state.IsCharging, "secondary identity and charging decode from a checksum-verified device frame");
        Assert(state.ObservedAtUtc == DateTime.MinValue, "parsing saved bytes cannot fabricate a fresh observation time");
        byte[] bad = (byte[])frame.Clone(); bad[58] ^= 1;
        Assert(!SoundcoreProtocol.TryReadDeviceState(bad, out state), "corrupt state cannot update a saved speaker identity");
        Assert(!SoundcoreProtocol.TryReadDeviceState(frame.Take(58).ToArray(), out state), "incomplete state cannot update saved information");
        bad = (byte[])frame.Clone(); bad[21] = 0; Checksum(bad);
        Assert(!SoundcoreProtocol.TryReadDeviceState(bad, out state), "unrecognized serial layout is rejected");
        SoundcoreProtocol.TryReadDeviceState(frame, out state);
        DateTime observed = new DateTime(2026,11,5,1,8,0,DateTimeKind.Utc);
        state.ObservedAtUtc = observed;
        var row = new PartyCastSpeakerRecord { PrimaryAddress="F4:9D:8A:0A:91:10", Address="F4:9D:8A:0E:D2:BB", AssociationVerifiedUtc=observed };
        var battery = new BatteryReading { Percentage=90, ReportedAtUtc=observed.AddMinutes(-2) };
        Assert(row.Apply(state,battery) && row.SerialNumber=="TESTSECONDARY001" && row.BatteryPercentage==90, "a newer state read and battery report update the saved partner and ask to be saved");
        Assert(!row.Apply(state,battery), "re-applying the same reports changes nothing, so nothing is rewritten to disk");
        bool changed = row.Apply(new GlowDeviceReport { SerialNumber="WRONG", ObservedAtUtc=observed.AddSeconds(-1) },new BatteryReading { Percentage=20, ReportedAtUtc=observed.AddHours(-1) });
        Assert(!changed && row.SerialNumber=="TESTSECONDARY001" && row.Charging==true && row.BatteryPercentage==90 && row.BatteryReportedAtUtc==observed.AddMinutes(-2), "older state and battery reports cannot overwrite newer device-specific observations");
        Assert(row.Apply(null,new BatteryReading { Percentage=70, ReportedAtUtc=observed.AddMinutes(5) }) && row.BatteryPercentage==70 && row.Charging==true, "a Windows battery report alone updates battery without touching the read state");
        string directory = Path.Combine(Path.GetTempPath(),"SoundcoreDesk-record-test-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory,"records.json");
        try
        {
            PartyCastRecords.Save(path,new[] { row, new PartyCastSpeakerRecord { PrimaryAddress=row.Address, Address=row.Address, AssociationVerifiedUtc=observed }, new PartyCastSpeakerRecord { PrimaryAddress=row.PrimaryAddress,Address="invalid",AssociationVerifiedUtc=observed } });
            var loaded=PartyCastRecords.Load(path);
            Assert(loaded.Count==1 && loaded[0].SerialNumber==row.SerialNumber && loaded[0].LastReadUtc.ToUniversalTime()==observed, "saved identities round-trip with timestamps and exclude invalid or self-linked associations");
            File.WriteAllText(path,"broken JSON");
            Assert(PartyCastRecords.Load(path).Count==0, "invalid record storage cannot fabricate a member list");
        }
        finally { if(File.Exists(path))File.Delete(path); if(File.Exists(path+".tmp"))File.Delete(path+".tmp"); if(Directory.Exists(directory))Directory.Delete(directory); }
        var now = new DateTime(2026, 11, 5, 11, 30, 0, DateTimeKind.Utc);
        var peer = new PartyCastSpeakerRecord { Address = "F4:9D:8A:00:D2:BB", Name = "soundcore Glow Mini", Firmware = "02.10", Charging = true,
            LastReadUtc = now.AddMinutes(-4), BatteryPercentage = 80, BatteryReportedAtUtc = now.AddMinutes(-5) };
        string line = PartyCastRecords.PeerSummary(peer, now);
        Assert(line.Contains("D2:BB") && line.Contains("電量 80%") && line.Contains("正在充電") && line.Contains(now.AddMinutes(-4).ToLocalTime().ToString("HH:mm") + " 讀取"), "PartyCast card summary carries battery, charging and the time each was read");
        Assert(!line.Contains("即時") && !line.Contains("已連線"), "PartyCast card summary never claims a live group connection");
        string unread = PartyCastRecords.PeerSummary(new PartyCastSpeakerRecord { Address = "F4:9D:8A:00:D2:BB", Charging = true }, now);
        Assert(unread.Contains("電量未取得") && unread.Contains("充電狀態未取得"), "a charging flag without a read time is not shown as current");
        return checks;
    }
}
