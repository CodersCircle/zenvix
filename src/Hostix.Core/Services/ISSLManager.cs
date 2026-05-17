namespace Hostix.Core.Services
{
    public interface ISSLManager
    {
        bool GenerateCert(string domain);
        bool IsCertValid(string domain);
        void PurgeCert(string domain);
        bool InstallRootCA();
    }
}
