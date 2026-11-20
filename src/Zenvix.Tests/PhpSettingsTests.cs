using System;
using System.IO;
using Xunit;
using Zenvix.Runtime.Services;

namespace Zenvix.Tests
{
    public class PhpSettingsTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "zenvix-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _ext;

        public PhpSettingsTests()
        {
            _ext = Path.Combine(_base, "ext");
            Directory.CreateDirectory(_ext);
            foreach (var n in new[] { "curl", "soap", "zend_test", "opcache", "mbstring" })
                File.WriteAllText(Path.Combine(_ext, $"php_{n}.dll"), "");
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, true); } catch { }
        }

        [Fact]
        public void Available_hides_test_dlls_and_lists_opcache()
        {
            var list = PhpExtensions.Available(_ext);
            Assert.Contains("soap", list);
            Assert.Contains("opcache", list);
            Assert.DoesNotContain("zend_test", list);
        }

        [Fact]
        public void Defaults_until_changed_then_toggles_persist()
        {
            var ini = PhpExtensions.IniLines(_ext, baseDir: _base);
            Assert.Contains("extension=curl", ini);
            Assert.DoesNotContain("extension=soap", ini);
            Assert.Contains("zend_extension=opcache", ini);

            PhpExtensions.SetEnabled("soap", true, _base);
            PhpExtensions.SetEnabled("curl", false, _base);
            ini = PhpExtensions.IniLines(_ext, baseDir: _base);
            Assert.Contains("extension=soap", ini);
            Assert.DoesNotContain("extension=curl", ini);
        }

        [Fact]
        public void Cli_ini_keeps_required_and_skips_zend()
        {
            PhpExtensions.SetEnabled("curl", false, _base);
            var ini = PhpExtensions.IniLines(_ext, new[] { "curl" }, includeZend: false, baseDir: _base);
            Assert.Contains("extension=curl", ini);
            Assert.DoesNotContain("opcache", ini);
        }

        [Fact]
        public void Active_php_version_round_trips()
        {
            Assert.Null(ActivePhpVersion.Get(_base));
            ActivePhpVersion.Set("8.5.0", _base);
            Assert.Equal("8.5.0", ActivePhpVersion.Get(_base));
        }
    }
}
