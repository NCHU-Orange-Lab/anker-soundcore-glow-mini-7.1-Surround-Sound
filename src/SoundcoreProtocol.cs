using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SoundcoreDesk
{
    public sealed class GlowDeviceReport
    {
        public string Firmware { get; set; }
        public string SerialNumber { get; set; }
        public bool IsCharging { get; set; }
        public bool PartyCastEnabled { get; set; }
        public DateTime ObservedAtUtc { get; set; }
    }
    public sealed class GlowChargingReport
    {
        public bool IsCharging { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string FrameHex { get; set; }
    }

    public sealed class GlowPartyCastReport
    {
        public bool Enabled { get; set; }
        /// <summary>Charging flag carried by the same 01 01 state frame (body offset 2); null when not 0/1.</summary>
        public bool? Charging { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string StateFrameHex { get; set; }
        public string SetterAckHex { get; set; }
    }

    /// <summary>A3136 charging queries and explicitly verified PartyCast 0/1 control.</summary>
    public static class SoundcoreProtocol
    {
        static readonly object ChannelGate = new object();
        public const string GlowServiceId = "0cf12d31-fac3-4553-bd80-d6832e7b3136";
        // 08 EE, outbound header, query 01 04, total length 10 LE, additive checksum.
        static readonly byte[] ChargingQuery = { 0x08, 0xEE, 0, 0, 0, 0x01, 0x04, 0x0A, 0, 0x05 };

        public static GlowChargingReport ReadCharging(string address)
        {
            lock (ChannelGate) return ReadChargingCore(address);
        }
        static GlowChargingReport ReadChargingCore(string address)
        {
            using (var socket = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3))
            {
                socket.SendTimeout = 1000;
                socket.ReceiveTimeout = 200;
                IAsyncResult pending = socket.BeginConnect(new BluetoothServiceEndpoint(address), null, null);
                using (var wait = pending.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(3000)) throw new TimeoutException("Glow Mini 專用通道連線逾時。");
                    socket.EndConnect(pending);
                }
                // RFCOMM connect completion can precede the firmware's command-reader readiness.
                Thread.Sleep(350);
                int sent = 0;
                while (sent < ChargingQuery.Length)
                {
                    int count = socket.Send(ChargingQuery, sent, ChargingQuery.Length - sent, SocketFlags.None);
                    if (count <= 0) throw new System.IO.IOException("Glow Mini 專用通道已中斷。");
                    sent += count;
                }
                var input = new List<byte>();
                var buffer = new byte[1024];
                var elapsed = Stopwatch.StartNew();
                bool retried = false;
                while (elapsed.ElapsedMilliseconds < 2500)
                {
                    if (!retried && elapsed.ElapsedMilliseconds >= 900)
                    {
                        socket.Send(ChargingQuery);
                        retried = true;
                    }
                    try
                    {
                        int count = socket.Receive(buffer);
                        if (count == 0) throw new System.IO.IOException("Glow Mini 已關閉專用通道。");
                        for (int i = 0; i < count; i++) input.Add(buffer[i]);
                        bool charging; string frame;
                        if (TryReadCharging(input, out charging, out frame))
                            return new GlowChargingReport { IsCharging = charging, ObservedAtUtc = DateTime.UtcNow, FrameHex = frame };
                        if (input.Count > 8192) throw new System.IO.IOException("Glow Mini 回應超過預期大小。");
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock) throw;
                    }
                }
                throw new TimeoutException("未收到 Glow Mini 的充電狀態回應（收到 " + input.Count + " bytes）。");
            }
        }

        public static bool TryReadCharging(IList<byte> input, out bool charging, out string frameHex)
        {
            charging = false; frameHex = null;
            for (int start = 0; start + 9 <= input.Count; start++)
            {
                if (input[start] != 0x09 || input[start + 1] != 0xFF || input[start + 2] != 0 || input[start + 3] != 0 || input[start + 4] != 1) continue;
                int length = input[start + 7] | (input[start + 8] << 8);
                if (length < 10 || length > 4096 || start + length > input.Count) continue;
                if (input[start + 5] != 1 || input[start + 6] != 4 || length != 11) continue;
                int sum = 0;
                for (int i = 0; i < length - 1; i++) sum += input[start + i];
                if ((sum & 255) != input[start + length - 1] || input[start + 9] > 1) continue;
                charging = input[start + 9] == 1;
                byte[] frame = new byte[length];
                for (int i = 0; i < length; i++) frame[i] = input[start + i];
                frameHex = BitConverter.ToString(frame).Replace("-", "").ToLowerInvariant();
                return true;
            }
            return false;
        }

        public static GlowPartyCastReport ReadPartyCast(string address)
        {
            lock (ChannelGate)
            using (var socket = Connect(address)) return ReadPartyCastState(socket);
        }

        public static GlowPartyCastReport SetPartyCast(string address, bool enabled)
        {
            lock (ChannelGate)
            using (var socket = Connect(address))
            {
                // Validate the known A3136 state layout before allowing a write.
                var before = ReadPartyCastState(socket);
                if (before.Enabled == enabled) return before;
                SendAll(socket, BuildPartyCastCommand(enabled));
                byte[] ack = null;
                try { ack = ReceiveFrame(socket, 0x01, 0xA4, 0, 1300); }
                catch (TimeoutException) { /* The independent readback establishes success. */ }
                var after = ReadPartyCastState(socket);
                if (after.Enabled != enabled) throw new System.IO.IOException("喇叭尚未確認 PartyCast 切換，請重新整理後再試。");
                after.SetterAckHex = ack == null ? null : ToHex(ack);
                return after;
            }
        }

        public static byte[] BuildPartyCastCommand(bool enabled)
        {
            byte[] frame = { 0x08, 0xEE, 0, 0, 0, 0x01, 0xA4, 0x0B, 0, enabled ? (byte)1 : (byte)0, 0 };
            int sum = 0; for (int i = 0; i < frame.Length - 1; i++) sum += frame[i];
            frame[frame.Length - 1] = (byte)(sum & 255); return frame;
        }

        public static bool TryReadDeviceState(IList<byte> input, out GlowDeviceReport report)
        {
            report = null; byte[] frame;
            if (!TryReadFrame(input, 1, 1, 49, out frame) || frame[11] > 1 || frame[41] > 1) return false;
            // Recovered official A3136AnalysisService: firmware at 16, serial at 21.
            for (int i = 16; i < 37; i++) if (frame[i] < 32 || frame[i] > 126) return false;
            report = new GlowDeviceReport {
                Firmware = System.Text.Encoding.ASCII.GetString(frame, 16, 5),
                SerialNumber = System.Text.Encoding.ASCII.GetString(frame, 21, 16),
                IsCharging = frame[11] == 1, PartyCastEnabled = frame[41] == 1
            };
            // Parsing bytes alone does not establish a fresh observation time.
            return true;
        }

        public static bool TryReadPartyCastState(IList<byte> input, out bool enabled, out string frameHex)
        {
            enabled = false; frameHex = null; byte[] frame;
            // A3136: state body is 49 bytes, PartyCast is body offset 32.
            if (!TryReadFrame(input, 1, 1, 49, out frame) || frame[9 + 32] > 1) return false;
            enabled = frame[9 + 32] == 1; frameHex = ToHex(frame); return true;
        }

        /// <summary>
        /// Body offset 2 of the 49-byte 01 01 state toggled 01 → 00 → 01 together with the
        /// dedicated 01 04 query during the plug/unplug/replug test, and the recovered official
        /// A3136 parser reads it as the charging flag. Reading it here avoids a second RFCOMM
        /// connection per poll. Values other than 0/1 are rejected rather than guessed.
        /// </summary>
        public static bool TryReadStateCharging(IList<byte> input, out bool charging)
        {
            charging = false; byte[] frame;
            if (!TryReadFrame(input, 1, 1, 49, out frame) || frame[9 + 2] > 1) return false;
            charging = frame[9 + 2] == 1; return true;
        }

        static GlowPartyCastReport ReadPartyCastState(Socket socket)
        {
            byte[] query = { 0x08, 0xEE, 0, 0, 0, 1, 1, 0x0A, 0, 2 };
            SendAll(socket, query);
            byte[] response;
            try { response = ReceiveFrame(socket, 1, 1, 49, 1600); }
            catch (TimeoutException) { SendAll(socket, query); response = ReceiveFrame(socket, 1, 1, 49, 1600); }
            bool enabled, charging; string frame;
            if (!TryReadPartyCastState(response, out enabled, out frame)) throw new System.IO.IOException("Glow Mini 回報了尚未識別的 PartyCast 狀態。");
            bool? chargingFlag = TryReadStateCharging(response, out charging) ? charging : (bool?)null;
            return new GlowPartyCastReport { Enabled = enabled, Charging = chargingFlag, ObservedAtUtc = DateTime.UtcNow, StateFrameHex = frame };
        }

        static Socket Connect(string address)
        {
            var socket = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3);
            try
            {
                socket.SendTimeout = 1000; socket.ReceiveTimeout = 200;
                IAsyncResult pending = socket.BeginConnect(new BluetoothServiceEndpoint(address), null, null);
                using (var wait = pending.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(3000)) throw new TimeoutException("Glow Mini 專用通道連線逾時。");
                    socket.EndConnect(pending);
                }
                Thread.Sleep(350); return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        static void SendAll(Socket socket, byte[] frame)
        {
            int offset = 0;
            while (offset < frame.Length)
            {
                int count = socket.Send(frame, offset, frame.Length - offset, SocketFlags.None);
                if (count <= 0) throw new System.IO.IOException("Glow Mini 專用通道已中斷。");
                offset += count;
            }
        }
        static byte[] ReceiveFrame(Socket socket, byte first, byte second, int bodyLength, int timeoutMs)
        {
            var input = new List<byte>(); var buffer = new byte[1024]; var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    int count = socket.Receive(buffer);
                    if (count == 0) throw new System.IO.IOException("Glow Mini 已關閉專用通道。");
                    for (int i = 0; i < count; i++) input.Add(buffer[i]);
                    byte[] frame; if (TryReadFrame(input, first, second, bodyLength, out frame)) return frame;
                    if (input.Count > 8192) throw new System.IO.IOException("Glow Mini 回應超過預期大小。");
                }
                catch (SocketException ex) { if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock) throw; }
            }
            throw new TimeoutException("未收到已驗證的 Glow Mini PartyCast 回應。");
        }
        static bool TryReadFrame(IList<byte> input, byte first, byte second, int bodyLength, out byte[] frame)
        {
            frame = null;
            for (int start = 0; start + 10 <= input.Count; start++)
            {
                if (input[start] != 9 || input[start + 1] != 255 || input[start + 2] != 0 || input[start + 3] != 0 || input[start + 4] != 1) continue;
                int length = input[start + 7] | (input[start + 8] << 8);
                if (length != bodyLength + 10 || start + length > input.Count || input[start + 5] != first || input[start + 6] != second) continue;
                int sum = 0; for (int i = 0; i < length - 1; i++) sum += input[start + i];
                if ((sum & 255) != input[start + length - 1]) continue;
                frame = new byte[length]; for (int i = 0; i < length; i++) frame[i] = input[start + i];
                return true;
            }
            return false;
        }
        static string ToHex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        sealed class BluetoothServiceEndpoint : EndPoint
        {
            readonly byte[] address;
            public BluetoothServiceEndpoint(string mac)
            {
                if (mac == null || mac.Replace(":", "").Length != 12) throw new ArgumentException("無效的藍牙位址。", "mac");
                address = BitConverter.GetBytes(Convert.ToUInt64(mac.Replace(":", ""), 16));
            }
            public override AddressFamily AddressFamily { get { return (AddressFamily)32; } }
            public override SocketAddress Serialize()
            {
                var value = new SocketAddress(AddressFamily, 30);
                for (int i = 0; i < 8; i++) value[2 + i] = address[i];
                byte[] service = new Guid(GlowServiceId).ToByteArray();
                for (int i = 0; i < 16; i++) value[10 + i] = service[i];
                // port 0 asks Winsock to resolve this specific SDP service UUID.
                return value;
            }
            public override EndPoint Create(SocketAddress value) { return this; }
        }
    }
}
