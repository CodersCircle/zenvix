using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Hostix.Core.Models
{
    public enum DbEngineType
    {
        // Local relational
        MariaDB,
        MySQL,
        PostgreSQL,
        SQLite,
        // Local NoSQL
        MongoDB,
        Redis,
        // Local search
        Meilisearch,
        // Cloud / BaaS
        Supabase,
        Firebase,
        PlanetScale,
        Neon,
    }

    public enum DbInstanceStatus
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error,
        NotInstalled,
        Cloud       // Cloud services are always "connected" or "disconnected"
    }

    public static class DbEngineDefaults
    {
        public static readonly Dictionary<DbEngineType, (string LatestVersion, int DefaultPort, string PanelPath, string Icon, bool IsCloud)> Metadata = new()
        {
            [DbEngineType.MariaDB]     = ("11.4",  3306,  "http://localhost:8080",                    "Ma",  false),
            [DbEngineType.MySQL]       = ("8.4",   3307,  "http://localhost:8080",                    "My",  false),
            [DbEngineType.PostgreSQL]  = ("16.3",  5432,  "http://localhost:5050",                    "Pg",  false),
            [DbEngineType.SQLite]      = ("3.45",  0,     "",                                         "SQ",  false),
            [DbEngineType.MongoDB]     = ("7.0",   27017, "http://localhost:8081",                    "Mo",  false),
            [DbEngineType.Redis]       = ("7.2",   6379,  "http://localhost:8001",                    "Re",  false),
            [DbEngineType.Meilisearch] = ("1.8",   7700,  "http://localhost:7700",                    "Me",  false),
            [DbEngineType.Supabase]    = ("cloud", 0,     "https://supabase.com/dashboard",           "Su",  true),
            [DbEngineType.Firebase]    = ("cloud", 0,     "https://console.firebase.google.com",      "Fi",  true),
            [DbEngineType.PlanetScale] = ("cloud", 0,     "https://app.planetscale.com",              "PS",  true),
            [DbEngineType.Neon]        = ("cloud", 0,     "https://console.neon.tech",                "Ne",  true),
        };

        public static string GetPanelLabel(DbEngineType engine) => engine switch
        {
            DbEngineType.MariaDB     => "phpMyAdmin",
            DbEngineType.MySQL       => "phpMyAdmin",
            DbEngineType.PostgreSQL  => "pgAdmin",
            DbEngineType.MongoDB     => "Mongo Express",
            DbEngineType.Redis       => "RedisInsight",
            DbEngineType.Meilisearch => "Dashboard",
            DbEngineType.Supabase    => "Open Dashboard",
            DbEngineType.Firebase    => "Open Console",
            DbEngineType.PlanetScale => "Open Console",
            DbEngineType.Neon        => "Open Console",
            _                        => "Adminer"
        };

        public static bool IsCloud(DbEngineType engine) =>
            Metadata.TryGetValue(engine, out var m) && m.IsCloud;
    }

    public class DatabaseInstance : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, string name)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public DbEngineType Engine { get; set; }
        public string Version { get; set; } = string.Empty;
        public int Port { get; set; }

        private DbInstanceStatus _status = DbInstanceStatus.Stopped;
        public DbInstanceStatus Status
        {
            get => _status;
            set => Set(ref _status, value, nameof(Status));
        }

        private int? _processId;
        public int? ProcessId
        {
            get => _processId;
            set => Set(ref _processId, value, nameof(ProcessId));
        }

        public string DataPath { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = string.Empty;
        public string LogsPath { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public bool AutoStart { get; set; } = false;
        public bool IsCloud => DbEngineDefaults.IsCloud(Engine);
        public DateTime? LastStarted { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string CpuUsage { get; set; } = "0%";
        public string RamUsage { get; set; } = "0 MB";

        public string PanelUrl   => DbEngineDefaults.Metadata.TryGetValue(Engine, out var m) ? m.PanelPath : "";
        public string Icon        => DbEngineDefaults.Metadata.TryGetValue(Engine, out var m) ? m.Icon : "DB";
        public string PanelLabel  => DbEngineDefaults.GetPanelLabel(Engine);
        public string EngineLabel => Engine.ToString();
    }
}
