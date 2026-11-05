using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SoundcoreDesk
{
    // An explicitly confirmed association is historical, never a live member report.
    public sealed class PartyCastSpeakerRecord
    {
        public string PrimaryAddress { get; set; }
        public string Address { get; set; }
        public string Name { get; set; }
        public string SerialNumber { get; set; }
        public string Firmware { get; set; }
        public DateTime AssociationVerifiedUtc { get; set; }
        public DateTime LastReadUtc { get; set; }
        public bool? Charging { get; set; }
        public int? BatteryPercentage { get; set; }
        public DateTime? BatteryReportedAtUtc { get; set; }

        /// <summary>Takes only reports newer than what is stored; returns whether anything changed (i.e. needs saving).</summary>
        public bool Apply(GlowDeviceReport report, BatteryReading battery)
        {
            bool changed = false;
            if (report != null && report.ObservedAtUtc > LastReadUtc)
            {
                SerialNumber = report.SerialNumber; Firmware = report.Firmware;
                Charging = report.IsCharging; LastReadUtc = report.ObservedAtUtc; changed = true;
            }
            // Keep the Windows source timestamp; refreshing the UI is not a report.
            if (battery != null && battery.ReportedAtUtc.HasValue && (!BatteryReportedAtUtc.HasValue || battery.ReportedAtUtc.Value > BatteryReportedAtUtc.Value))
            { BatteryPercentage = battery.Percentage; BatteryReportedAtUtc = battery.ReportedAtUtc; changed = true; }
            return changed;
        }
    }

    public static class PartyCastRecords
    {
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundcoreDesk", "partycast-speakers.json"); } }
        static bool ValidAddress(string address)
        {
            if (address == null || address.Length != 17) return false;
            for (int i = 0; i < address.Length; i++)
                if (i % 3 == 2 ? address[i] != ':' : !Uri.IsHexDigit(address[i])) return false;
            return true;
        }
        public static List<PartyCastSpeakerRecord> Load(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) return new List<PartyCastSpeakerRecord>();
                var rows = new JavaScriptSerializer().Deserialize<List<PartyCastSpeakerRecord>>(File.ReadAllText(path)) ?? new List<PartyCastSpeakerRecord>();
                return rows.Where(r => r != null && ValidAddress(r.PrimaryAddress) && ValidAddress(r.Address) && !string.Equals(r.PrimaryAddress, r.Address, StringComparison.OrdinalIgnoreCase) && r.AssociationVerifiedUtc != DateTime.MinValue)
                    .Select(r => { r.Address = r.Address.ToUpperInvariant(); r.PrimaryAddress = r.PrimaryAddress.ToUpperInvariant(); return r; })
                    .GroupBy(r => r.PrimaryAddress + "/" + r.Address).Select(g => g.OrderByDescending(r => r.LastReadUtc).First()).Take(64).ToList();
            }
            catch { return new List<PartyCastSpeakerRecord>(); }
        }
        public static void Save(string path, IEnumerable<PartyCastSpeakerRecord> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(rows), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        /// <summary>One line for the PartyCast card. Every value carries its own source time; nothing is presented as live.</summary>
        public static string PeerSummary(PartyCastSpeakerRecord row, DateTime utcNow)
        {
            Func<DateTime, string> when = t => { var local = t.ToLocalTime(); return local.Date == utcNow.ToLocalTime().Date ? local.ToString("HH:mm") : local.ToString("MM/dd HH:mm"); };
            string name = (string.IsNullOrEmpty(row.Name) ? "soundcore Glow Mini" : row.Name) + " · " + (row.Address != null && row.Address.Length == 17 ? row.Address.Substring(12) : "位址未知");
            string battery = row.BatteryPercentage.HasValue ? "電量 " + row.BatteryPercentage.Value + "%" + (row.BatteryReportedAtUtc.HasValue ? "（" + when(row.BatteryReportedAtUtc.Value) + "）" : "") : "電量未取得";
            string charge = !row.Charging.HasValue || row.LastReadUtc == DateTime.MinValue ? "充電狀態未取得" : (row.Charging.Value ? "正在充電" : "未在充電") + "（" + when(row.LastReadUtc) + " 讀取）";
            string identity = row.Firmware == null ? "" : " · 韌體 " + row.Firmware;
            return name + " — " + battery + " · " + charge + identity;
        }
    }
}
