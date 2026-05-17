using System;
using System.Collections.Concurrent;
using Hostix.Core.Models;

namespace Hostix.Runtime.Services
{
    public interface IDatabaseCredentialsManager
    {
        (string Username, string Password) GetCredentials(RuntimeServiceType type);
        void UpdatePassword(RuntimeServiceType type, string newPassword);
    }

    public class DatabaseCredentialsManager : IDatabaseCredentialsManager
    {
        private readonly ConcurrentDictionary<RuntimeServiceType, (string Username, string Password)> _creds = new();

        public DatabaseCredentialsManager()
        {
            // Set defaults for supported databases
            _creds[RuntimeServiceType.MySQL]      = ("root", "");
            _creds[RuntimeServiceType.MariaDB]    = ("root", "");
            _creds[RuntimeServiceType.PostgreSQL] = ("postgres", "");
        }

        public (string Username, string Password) GetCredentials(RuntimeServiceType type)
        {
            if (_creds.TryGetValue(type, out var val)) return val;
            return ("root", ""); // Fallback
        }

        public void UpdatePassword(RuntimeServiceType type, string newPassword)
        {
            var current = GetCredentials(type);
            _creds[type] = (current.Username, newPassword);
        }
    }
}
