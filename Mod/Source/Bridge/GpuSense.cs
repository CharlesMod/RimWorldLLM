using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace HermesVoice.Bridge
{
    /// <summary>The graphics adapter the game renders on, as the game's engine reports it. Verse-free.</summary>
    public sealed class HostAdapterInfo
    {
        public string Name;
        public int VendorId;
        public int MemoryMiB;

        public HostAdapterInfo() { }
        public HostAdapterInfo(string name, int vendorId, int memoryMiB) { Name = name; VendorId = vendorId; MemoryMiB = memoryMiB; }

        /// <summary>True when there is anything worth sending.</summary>
        public bool Known { get { return !string.IsNullOrWhiteSpace(Name) || VendorId != 0; } }

        public Newtonsoft.Json.Linq.JObject ToJson()
        {
            return new Newtonsoft.Json.Linq.JObject
            {
                ["name"] = Name ?? "",
                ["vendor_id"] = VendorId,
                ["memory_mib"] = Math.Max(0, MemoryMiB)
            };
        }
    }

    /// <summary>One graphics adapter in status.gpu.adapters.</summary>
    public sealed class GpuAdapter
    {
        [JsonProperty("index")] public int Index;
        [JsonProperty("vendor")] public string Vendor;
        [JsonProperty("vendor_id")] public int VendorId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("dedicated_mib")] public int DedicatedMiB;
        [JsonProperty("shared_mib")] public int SharedMiB;
        [JsonProperty("used_mib")] public int UsedMiB;
        [JsonProperty("integrated")] public bool Integrated;
    }

    public sealed class GpuReserve
    {
        [JsonProperty("others_mib")] public int OthersMiB;
        [JsonProperty("others_basis")] public string OthersBasis;
        [JsonProperty("host_mib")] public int HostMiB;
        [JsonProperty("host_basis")] public string HostBasis;
        [JsonProperty("margin_mib")] public int MarginMiB;
        [JsonProperty("total_mib")] public int TotalMiB;
    }

    /// <summary>status.gpu (older engines have none: the field stays null).</summary>
    public sealed class GpuSummary
    {
        [JsonProperty("adapters")] public List<GpuAdapter> Adapters;
        [JsonProperty("engine_adapter")] public int EngineAdapter = -1;
        [JsonProperty("host_adapter")] public int HostAdapter = -1;
        [JsonProperty("host_integrated")] public string HostIntegrated;
        [JsonProperty("reserve")] public GpuReserve Reserve;
        [JsonProperty("free_for_model_mib")] public int FreeForModelMiB;
        [JsonProperty("others_note")] public string OthersNote;

        public GpuAdapter Find(int index)
        {
            if (Adapters == null || index < 0) return null;
            foreach (var a in Adapters) if (a != null && a.Index == index) return a;
            return null;
        }
    }

    /// <summary>The one status line about the graphics card. Pure: the translator is passed in (key, args) so tests and the game share it.</summary>
    public static class GpuLine
    {
        public const string KeyHostAndModel = "HV_Gpu_HostAndModel";   // Game on {0} · {1}: {2} free for the model
        public const string KeyModelOnly = "HV_Gpu_ModelOnly";         // {0}: {1} free for the model
        public const string KeyShared = "HV_Gpu_Shared";               // Game and model share {0}: {1} free for the model
        public const string KeyCpu = "HV_Gpu_Cpu";                     // No graphics card in use: the model runs on the processor

        static readonly Regex Paren = new Regex(@"\((?:R|TM|C)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Noise = new Regex(@"\b(?:NVIDIA|GeForce|AMD|Laptop GPU|Notebook|Max-Q|with Max-Q Design|Graphics|GPU|Series)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>"NVIDIA GeForce RTX 3050 Laptop GPU" -> "RTX 3050"; "Intel(R) Iris(R) Xe Graphics" -> "Intel Iris Xe".</summary>
        public static string ShortName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = Paren.Replace(name, "");
            s = Noise.Replace(s, " ");
            s = Spaces.Replace(s, " ").Trim(' ', '-', ',');
            return s.Length == 0 ? name.Trim() : s;
        }

        /// <summary>GB with one decimal from MiB / 1024, culture-neutral.</summary>
        public static string Gb(int mib)
        {
            return (Math.Max(0, mib) / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        /// <summary>The line, or null when the engine sent no gpu data.</summary>
        public static string Format(GpuSummary g, Func<string, object[], string> t)
        {
            if (g == null || t == null) return null;
            string line;
            var eng = g.Find(g.EngineAdapter);
            if (g.EngineAdapter < 0) line = t(KeyCpu, new object[0]);
            else if (eng == null) return null;   // an index we cannot resolve: say nothing rather than guess
            else
            {
                string en = ShortName(eng.Name);
                string free = Gb(g.FreeForModelMiB);
                var host = g.Find(g.HostAdapter);
                if (host == null) line = t(KeyModelOnly, new object[] { en, free });
                else if (host.Index == eng.Index) line = t(KeyShared, new object[] { en, free });
                else line = t(KeyHostAndModel, new object[] { ShortName(host.Name), en, free });
            }
            if (!string.IsNullOrWhiteSpace(g.OthersNote)) line = line.TrimEnd() + (line.EndsWith(".") ? " " : ". ") + g.OthersNote.Trim();
            return line;
        }
    }
}
