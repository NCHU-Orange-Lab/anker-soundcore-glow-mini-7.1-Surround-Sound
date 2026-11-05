using System;
using System.Collections.Generic;
using System.Linq;
using SoundcoreDesk;

static class DeviceIdentityTests
{
    static int checks;
    static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++; Console.WriteLine("PASS: " + label);
    }
    public static int Main()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), other = Guid.NewGuid();
        var snapshot = new Snapshot {
            Bluetooth = new List<BluetoothDevice> {
                new BluetoothDevice { Name = "soundcore Glow Mini", ContainerId = first, Address = "F4:9D:8A:0A:91:10", Connected = true },
                new BluetoothDevice { Name = "soundcore Glow Mini", ContainerId = second, Address = "F4:9D:8A:0A:91:11", Connected = false },
                new BluetoothDevice { Name = "Other speaker", ContainerId = other, Address = "11:22:33:44:55:66", Connected = true }
            },
            Audio = new List<AudioDevice> {
                new AudioDevice { Id = "b-offline", Name = "soundcore Glow Mini", ContainerId = second, State = 8 },
                new AudioDevice { Id = "a-call", Name = "soundcore Glow Mini Hands-Free", ContainerId = first, State = 1, IsDefault = true },
                new AudioDevice { Id = "a-stereo", Name = "My renamed speaker", ContainerId = first, State = 1 },
                new AudioDevice { Id = "other", Name = "Other speaker", ContainerId = other, State = 1 },
                new AudioDevice { Id = "unmapped", Name = "soundcore Glow Mini", ContainerId = Guid.Empty, State = 8 }
            }
        };
        snapshot.AssociateContainers();
        Assert(snapshot.Audio[0].BluetoothAddress == "F4:9D:8A:0A:91:11", "identical names retain separate physical identities");
        Assert(snapshot.Audio[2].BluetoothAddress == "F4:9D:8A:0A:91:10", "renamed stereo endpoint maps by container");
        Assert(snapshot.Audio[1].BluetoothAddress == snapshot.Audio[2].BluetoothAddress, "call and stereo endpoints map to the same speaker");
        Assert(snapshot.Audio[4].BluetoothAddress == null, "missing container never borrows another speaker address");
        var endpoints = snapshot.GlowEndpoints();
        Assert(endpoints[0].Id == "a-stereo", "startup ordering prefers available stereo over call audio");
        Assert(!endpoints.Any(d => d.Id == "other"), "unrelated output is excluded from speaker controls");
        Assert(!endpoints.First(d => d.Id == "b-offline").Connected, "paired offline speaker is not reported connected");
        Assert(endpoints[0].ToString().Contains("91:10"), "device lists expose the physical identity suffix");
        var cards = snapshot.ConnectedSpeakers();
        Assert(cards.Count == 1 && cards[0].Id == "a-stereo", "home cards: one per connected speaker, stereo only — no call audio, offline or unrelated outputs");
        Guid frontPair = Guid.NewGuid(), rearPair = Guid.NewGuid();
        var tws = new Snapshot {
            Bluetooth = new List<BluetoothDevice> {
                new BluetoothDevice { Name = "soundcore Glow Mini", ContainerId = frontPair, Address = "F4:9D:8A:0E:D2:BB", Connected = true },
                new BluetoothDevice { Name = "soundcore Glow Mini", ContainerId = rearPair, Address = "F4:9D:8A:0A:91:10", Connected = true }
            },
            Audio = new List<AudioDevice> {
                new AudioDevice { Id = "front", Name = "喇叭 (2- soundcore Glow Mini)", ContainerId = frontPair, State = 1 },
                new AudioDevice { Id = "front-call", Name = "soundcore Glow Mini Hands-Free", ContainerId = frontPair, State = 1 },
                new AudioDevice { Id = "rear", Name = "喇叭 (4- soundcore Glow Mini)", ContainerId = rearPair, State = 1 },
                new AudioDevice { Id = "rear-stale", Name = "喇叭 (soundcore Glow Mini)", ContainerId = rearPair, State = 8 },
                new AudioDevice { Id = "cable", Name = "CABLE Input (VB-Audio Virtual Cable)", ContainerId = Guid.NewGuid(), State = 1, IsDefault = true }
            }
        };
        tws.AssociateContainers();
        var pairs = tws.ConnectedSpeakers().Select(d => d.Id).OrderBy(id => id).ToList();
        Assert(pairs.SequenceEqual(new[] { "front", "rear" }), "two TWS main speakers give two cards; the stale endpoint and the virtual cable do not");
        var twin = new Snapshot { Bluetooth = new List<BluetoothDevice>(), Audio = new List<AudioDevice> {
            new AudioDevice { Id = "x1", Name = "soundcore Glow Mini", ContainerId = frontPair, State = 1 },
            new AudioDevice { Id = "x2", Name = "soundcore Glow Mini", ContainerId = frontPair, State = 1 } } };
        Assert(twin.ConnectedSpeakers().Count == 1, "one speaker exposing two stereo endpoints still gets a single card");
        Assert(SpeakerCard.Suffix("F4:9D:8A:0E:D2:BB") == "D2:BB" && SpeakerCard.Suffix(null) == null, "cards are told apart by the last two address bytes");
        snapshot.Bluetooth.RemoveAt(0); snapshot.AssociateContainers();
        Assert(snapshot.Audio[2].BluetoothAddress == null, "removed physical device clears old address");
        var reported = new DateTime(2026, 11, 4, 18, 10, 9, DateTimeKind.Utc);
        var battery = BatteryReading.Parse(new byte[] { 90 }, 3, BitConverter.GetBytes(reported.ToFileTimeUtc()), 16, null, 0, "hfp");
        Assert(battery.Percentage == 90 && battery.ReportedAtUtc == reported, "battery report retains its actual UTC source timestamp");
        Assert(!battery.Charging.HasValue, "battery level alone never establishes charging");
        Assert(BatteryReading.Parse(new byte[] { 0 }, 3, null, 0, null, 0, "hfp").Percentage == 0, "zero battery is valid");
        Assert(BatteryReading.Parse(new byte[] { 100 }, 3, null, 0, null, 0, "hfp").Charging == null, "100 percent never implies fully charged");
        Assert(BatteryReading.Parse(new byte[] { 101 }, 3, null, 0, null, 0, "hfp") == null, "out-of-range battery is unknown");
        Assert(BatteryReading.Parse(new byte[] { 90 }, 7, null, 0, null, 0, "hfp") == null, "power-management data is not interpreted as battery");
        Assert(BatteryReading.Parse(new byte[] { 90 }, 3, null, 0, new byte[] { 1 }, 3, "hfp").Charging == true, "explicit charging status is supported");
        Assert(BatteryReading.Parse(new byte[] { 90 }, 3, null, 0, new byte[] { 2 }, 3, "hfp").Charging == null, "unknown charging enum is not treated as true");
        Assert(BatteryReading.Parse(new byte[] { 90 }, 3, BitConverter.GetBytes(long.MaxValue), 16, null, 0, "hfp").ReportedAtUtc == null, "invalid timestamp does not fabricate a report time");
        BatteryReading.Attach(snapshot.Bluetooth, first, battery);
        Assert(snapshot.Bluetooth.All(d => d.Battery == null), "battery from a removed container never leaks onto an identical name");
        var sameName = new BluetoothDevice { Name = "soundcore Glow Mini", ContainerId = first };
        snapshot.Bluetooth.Add(sameName); BatteryReading.Attach(snapshot.Bluetooth, first, battery);
        var older = BatteryReading.Parse(new byte[] { 40 }, 3, BitConverter.GetBytes(reported.AddHours(-1).ToFileTimeUtc()), 16, null, 0, "old-node");
        BatteryReading.Attach(snapshot.Bluetooth, first, older);
        Assert(sameName.Battery.Percentage == 90, "older service report cannot replace a newer battery report");
        BatteryReading.Attach(snapshot.Bluetooth, Guid.Empty, older);
        Assert(snapshot.Bluetooth.First(d => d.ContainerId == second).Battery == null, "battery mapping remains container-specific");
        checks += ProtocolTests.Run();
        checks += PartyCastRecordTests.Run();
        checks += SyncTestTests.Run();
        checks += SurroundTests.Run();
        Console.WriteLine(checks + " checks passed."); return 0;
    }
}
