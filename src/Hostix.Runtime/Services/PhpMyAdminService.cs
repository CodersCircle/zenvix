using System.Threading.Tasks;

namespace Hostix.Runtime.Services
{
    public interface IPhpMyAdminService
    {
        event Action<string>? OnStatusMessage;
        Task OpenPanelAsync(string dbType, int dbPort,
            string username = "root", string password = "", string database = "");
        void Dispose();
    }

    /// <summary>
    /// Thin facade over EmbeddedToolsOrchestrator for phpMyAdmin integration.
    /// All logic lives in EmbeddedToolsOrchestrator.
    /// </summary>
    public class PhpMyAdminService : IPhpMyAdminService
    {
        private readonly IEmbeddedToolsOrchestrator _tools;
        public event Action<string>? OnStatusMessage;

        public PhpMyAdminService(IEmbeddedToolsOrchestrator tools)
        {
            _tools = tools;
            _tools.OnStatusMessage += msg => {
                Serilog.Log.Information("[phpMyAdmin] {Msg}", msg);
                OnStatusMessage?.Invoke(msg);
            };
        }

        public Task OpenPanelAsync(string dbType, int dbPort,
            string username = "root", string password = "", string database = "")
            => _tools.OpenDatabasePanelAsync(dbType, "127.0.0.1", dbPort, username, password, database)
                     .ContinueWith(_ => { });

        public void Dispose() { }
    }
}
