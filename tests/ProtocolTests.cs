using System;
using System.Collections.Generic;
using System.Linq;
using SoundcoreDesk;

static class ProtocolTests
{
    static int checks;
    static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++; Console.WriteLine("PASS: " + label);
    }
    static byte[] Hex(string text)
    { return Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray(); }
    public static int Run()
    {
        // Actual A3136 captures with USB-C connected / disconnected / reconnected.
        byte[] plugged = Hex("09ff00000101040b00011a");
        byte[] unplugged = Hex("09ff00000101040b000019");
        bool charging; string frame;
        Assert(SoundcoreProtocol.TryReadCharging(plugged, out charging, out frame) && charging, "actual plugged-in Glow Mini reports charging");
        Assert(SoundcoreProtocol.TryReadCharging(unplugged, out charging, out frame) && !charging, "actual unplugged Glow Mini reports not charging");
        var fragments = new List<byte>(); bool premature = false;
        for (int i = 0; i < plugged.Length - 1; i++) { fragments.Add(plugged[i]); premature |= SoundcoreProtocol.TryReadCharging(fragments, out charging, out frame); }
        fragments.Add(plugged[plugged.Length - 1]);
        Assert(!premature && SoundcoreProtocol.TryReadCharging(fragments, out charging, out frame) && charging, "fragmented socket reads wait for a complete verified frame");
        byte[] corrupted = (byte[])plugged.Clone(); corrupted[10] ^= 1;
        Assert(!SoundcoreProtocol.TryReadCharging(corrupted, out charging, out frame), "bad checksum cannot establish charging");
        Assert(!SoundcoreProtocol.TryReadCharging(Hex("09ff00000101040b00021b"), out charging, out frame), "unknown flag never becomes not-charging or fully-charged");
        Assert(!SoundcoreProtocol.TryReadCharging(Hex("09ff00000101030b00051d"), out charging, out frame), "battery level frame is not a charging response");
        Assert(SoundcoreProtocol.TryReadCharging(new byte[] { 0xAA, 0xBB }.Concat(corrupted).Concat(unplugged).ToArray(), out charging, out frame) && !charging, "stream framing recovers after noise and corrupt frames");
        Assert(SoundcoreProtocol.BuildPartyCastCommand(false).SequenceEqual(Hex("08ee00000001a40b0000a6")), "PartyCast off command matches the verified hardware write");
        Assert(SoundcoreProtocol.BuildPartyCastCommand(true).SequenceEqual(Hex("08ee00000001a40b0001a7")), "PartyCast on command matches the verified hardware write");
        // Actual A3136 response with only its 16-byte serial number redacted and checksum updated.
        byte[] partyState = Hex("09ff00000101013b000805010001010230322e313000000000000000000000000000000000013100000123ffff022828281e141e1e1e14000204bd");
        bool enabled;
        Assert(SoundcoreProtocol.TryReadPartyCastState(partyState, out enabled, out frame) && enabled, "actual A3136 state layout reports PartyCast on");
        byte[] offState = (byte[])partyState.Clone(); offState[41] = 0; offState[58]--;
        Assert(SoundcoreProtocol.TryReadPartyCastState(offState, out enabled, out frame) && !enabled, "PartyCast state is read independently from the setter ACK");
        Assert(!SoundcoreProtocol.TryReadPartyCastState(Hex("09ff00000101a40a00b8"), out enabled, out frame), "setter ACK alone never reports a successful PartyCast change");
        Assert(!SoundcoreProtocol.TryReadPartyCastState(Hex("09ff00000101240b00013a"), out enabled, out frame), "button notification alone is not an independent state readback");
        Assert(!SoundcoreProtocol.TryReadPartyCastState(partyState.Take(58).ToArray(), out enabled, out frame), "truncated PartyCast state is rejected");
        byte[] unknown = (byte[])partyState.Clone(); unknown[41] = 2; unknown[58]++;
        Assert(!SoundcoreProtocol.TryReadPartyCastState(unknown, out enabled, out frame), "unknown PartyCast flag cannot be interpreted as off");
        byte[] invalid = (byte[])partyState.Clone(); invalid[58] ^= 1;
        Assert(!SoundcoreProtocol.TryReadPartyCastState(invalid, out enabled, out frame), "bad PartyCast state checksum is rejected");
        invalid = (byte[])partyState.Clone(); invalid[7]++;
        Assert(!SoundcoreProtocol.TryReadPartyCastState(invalid, out enabled, out frame), "changed firmware state layout cannot enable a blind write");
        // Charging now comes from the same 01 01 state frame (body offset 2 = frame byte 11).
        Assert(SoundcoreProtocol.TryReadStateCharging(partyState, out charging) && charging, "actual A3136 state frame carries the charging flag");
        byte[] notCharging = (byte[])partyState.Clone(); notCharging[11] = 0; notCharging[58]--;
        Assert(SoundcoreProtocol.TryReadStateCharging(notCharging, out charging) && !charging, "state frame charging flag 00 reads as not charging");
        byte[] unknownCharge = (byte[])partyState.Clone(); unknownCharge[11] = 2; unknownCharge[58]++;
        Assert(!SoundcoreProtocol.TryReadStateCharging(unknownCharge, out charging), "unknown state charging flag is rejected, not guessed");
        invalid = (byte[])notCharging.Clone(); invalid[58] ^= 1;
        Assert(!SoundcoreProtocol.TryReadStateCharging(invalid, out charging), "bad state checksum cannot establish charging");
        Assert(!SoundcoreProtocol.TryReadStateCharging(plugged, out charging), "dedicated 01 04 frame is not mistaken for a state frame");
        // The capture's serial was redacted to zeros; give it a printable placeholder and fix the checksum.
        byte[] withSerial = (byte[])notCharging.Clone();
        for (int i = 21; i < 37; i++) withSerial[i] = (byte)'X';
        int sum = 0; for (int i = 0; i < withSerial.Length - 1; i++) sum += withSerial[i]; withSerial[withSerial.Length - 1] = (byte)(sum & 255);
        GlowDeviceReport identity;
        Assert(SoundcoreProtocol.TryReadDeviceState(withSerial, out identity) && !identity.IsCharging && SoundcoreProtocol.TryReadStateCharging(withSerial, out charging) && !charging, "identity parser and state charging parser agree on the same byte");
        return checks;
    }
}
