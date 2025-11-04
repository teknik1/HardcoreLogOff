using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Newtonsoft.Json;

namespace HardcoreLogOff
{
    /// <summary>
    /// HLOConfig = single source of truth for the mod configuration.
    /// </summary>
    public sealed class HLOConfig
    {
        // ======================== Schema versioning ========================
        public string ConfigVersion { get; set; } = "1.0.0";  // Update in core.cs too

        // ======================== Zoning / capture ========================
        public int ZoneSizeBlocks { get; set; } = 128;
        public int CaptureRadiusBlocks { get; set; } = 48;

        // ======================== Restore timings / retries ========================
        public int RestoreDelayMsAfterJoin { get; set; } = 100;
        public int RetryIntervalMs { get; set; } = 333;
        public int MaxRestoreAttempts { get; set; } = 3;
        public int ChunkRadiusPin { get; set; } = 2;
        public bool ForceRestoreIfStillNotReady { get; set; } = true;

        // ======================== Pin after restore ========================
        public int KeepPinnedAfterRestoreMs { get; set; } = 5000;

        // ======================== Snapshot TTL (real time) ========================
        public int SnapshotTtlMinMinutes { get; set; } = 120;
        public int SnapshotTtlMaxMinutes { get; set; } = 360;

        // ======================== Anti-despawn grace ========================
        public int GraceSecondsAfterRestore { get; set; } = 0;

        // ======================== Max snapshot size ========================
        public bool UseMaxMobs { get; set; } = true;
        public int MaxMobsSave { get; set; } = 60;

        // ======================== Category filters (by code prefix) ========================

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> SavePrefixes { get; set; } = new List<string>();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> TargetRestorePrefixes { get; set; } = new List<string>();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> PresenceTriggers { get; set; } = new List<string>();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> ExcludePrefixes { get; set; } = new List<string>();

        // ======================== Logging ========================
        public bool VerboseLogging { get; set; } = false;

        // ======================== Factory ========================
        public static HLOConfig MakeDefault(string version)
        {
            return new HLOConfig
            {
                ConfigVersion = version,

                // Defaults Values
                SavePrefixes = new List<string> { "drifter", "shiver", "bowtorn", "locust", "bell" },
                TargetRestorePrefixes = new List<string> { "drifter", "shiver", "bowtorn", "locust", "bell" },
                PresenceTriggers = new List<string> { "locust", "bell" },
                ExcludePrefixes = new List<string> { "locust-hacked" }
            };
        }

        // ======================== Centralized load/upgrade API ========================
        public static HLOConfig LoadOrCreate(
            ICoreServerAPI sapi,
            string cfgPath,
            string backupPath,
            string currentSchemaVersion,
            bool copyFieldsFromOldOnUpgrade = true)
        {
            var loaded = sapi.LoadModConfig<HLOConfig>(cfgPath);

            if (loaded == null)
            {
                var def = MakeDefault(currentSchemaVersion);
                def.NormalizeLists();
                sapi.StoreModConfig(def, cfgPath);
                sapi.Logger.Notification($"[HLO] Wrote new default config: {cfgPath}");
                return def;
            }

            loaded.NormalizeLists();

            if (!string.Equals(loaded.ConfigVersion, currentSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                sapi.Logger.Warning($"[HLO] Config schema '{loaded.ConfigVersion ?? "null"}' ≠ '{currentSchemaVersion}'. Backing up and rewriting defaults.");

                // --- Backup ---
                try
                {
                    var cleanBackup = loaded.CloneShallow();
                    cleanBackup.NormalizeLists();
                    sapi.StoreModConfig(cleanBackup, backupPath);
                }
                catch { /* best-effort */ }

                // Fresh defaults + migration soft
                var fresh = MakeDefault(currentSchemaVersion);
                if (copyFieldsFromOldOnUpgrade)
                {
                    TryMigrate(source: loaded, target: fresh);
                }
                fresh.NormalizeLists();

                sapi.StoreModConfig(fresh, cfgPath);
                return fresh;
            }

            sapi.StoreModConfig(loaded, cfgPath);
            return loaded;
        }

        // ======================== Migration helper ========================
        private static void TryMigrate(HLOConfig source, HLOConfig target)
        {
            try
            {
                if (source.SavePrefixes != null)
                    target.SavePrefixes = DedupPreserveOrder(source.SavePrefixes);
                if (source.TargetRestorePrefixes != null)
                    target.TargetRestorePrefixes = DedupPreserveOrder(source.TargetRestorePrefixes);
                if (source.PresenceTriggers != null)
                    target.PresenceTriggers = DedupPreserveOrder(source.PresenceTriggers);
                if (source.ExcludePrefixes != null)
                    target.ExcludePrefixes = DedupPreserveOrder(source.ExcludePrefixes);

                target.ZoneSizeBlocks = source.ZoneSizeBlocks;
                target.CaptureRadiusBlocks = source.CaptureRadiusBlocks;
                target.RestoreDelayMsAfterJoin = source.RestoreDelayMsAfterJoin;
                target.RetryIntervalMs = source.RetryIntervalMs;
                target.MaxRestoreAttempts = source.MaxRestoreAttempts;
                target.ChunkRadiusPin = source.ChunkRadiusPin;
                target.ForceRestoreIfStillNotReady = source.ForceRestoreIfStillNotReady;
                target.KeepPinnedAfterRestoreMs = source.KeepPinnedAfterRestoreMs;
                target.SnapshotTtlMinMinutes = source.SnapshotTtlMinMinutes;
                target.SnapshotTtlMaxMinutes = source.SnapshotTtlMaxMinutes;
                target.GraceSecondsAfterRestore = source.GraceSecondsAfterRestore;
                target.VerboseLogging = source.VerboseLogging;

                target.UseMaxMobs = source.UseMaxMobs;
                target.MaxMobsSave = source.MaxMobsSave;
            }
            catch
            {
                // defaults on any failure
            }
        }

        // ======================== Normalization / helpers ========================
        public void NormalizeLists()
        {
            SavePrefixes = DedupPreserveOrder(SavePrefixes);
            TargetRestorePrefixes = DedupPreserveOrder(TargetRestorePrefixes);
            PresenceTriggers = DedupPreserveOrder(PresenceTriggers);
            ExcludePrefixes = DedupPreserveOrder(ExcludePrefixes);
        }

        private static List<string> DedupPreserveOrder(IEnumerable<string> src)
        {
            if (src == null) return new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var s in src)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                var t = s.Trim();
                if (seen.Add(t)) list.Add(t);
            }
            return list;
        }

        private HLOConfig CloneShallow()
        {
            return new HLOConfig
            {
                ConfigVersion = this.ConfigVersion,
                ZoneSizeBlocks = this.ZoneSizeBlocks,
                CaptureRadiusBlocks = this.CaptureRadiusBlocks,
                RestoreDelayMsAfterJoin = this.RestoreDelayMsAfterJoin,
                RetryIntervalMs = this.RetryIntervalMs,
                MaxRestoreAttempts = this.MaxRestoreAttempts,
                ChunkRadiusPin = this.ChunkRadiusPin,
                ForceRestoreIfStillNotReady = this.ForceRestoreIfStillNotReady,
                KeepPinnedAfterRestoreMs = this.KeepPinnedAfterRestoreMs,
                SnapshotTtlMinMinutes = this.SnapshotTtlMinMinutes,
                SnapshotTtlMaxMinutes = this.SnapshotTtlMaxMinutes,
                GraceSecondsAfterRestore = this.GraceSecondsAfterRestore,
                UseMaxMobs = this.UseMaxMobs,
                MaxMobsSave = this.MaxMobsSave,
                SavePrefixes = this.SavePrefixes != null ? new List<string>(this.SavePrefixes) : new List<string>(),
                TargetRestorePrefixes = this.TargetRestorePrefixes != null ? new List<string>(this.TargetRestorePrefixes) : new List<string>(),
                PresenceTriggers = this.PresenceTriggers != null ? new List<string>(this.PresenceTriggers) : new List<string>(),
                ExcludePrefixes = this.ExcludePrefixes != null ? new List<string>(this.ExcludePrefixes) : new List<string>(),
                VerboseLogging = this.VerboseLogging
            };
        }
    }
}
