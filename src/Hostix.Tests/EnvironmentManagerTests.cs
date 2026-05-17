using System.IO;
using Hostix.Modules.Services;
using Xunit;

namespace Hostix.Tests
{
    public class EnvironmentManagerTests
    {
        [Fact]
        public void SetEnvValue_ShouldCreateKey_WhenKeyDoesNotExist()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            var envPath = Path.Combine(tempDir, ".env");
            File.WriteAllText(envPath, "APP_NAME=Hostix\n");
            
            var manager = new EnvironmentManager();

            // Act
            manager.SetEnvValue(tempDir, "DB_CONNECTION", "mysql");

            // Assert
            var lines = File.ReadAllLines(envPath);
            Assert.Contains("DB_CONNECTION=mysql", lines);
            
            // Cleanup
            Directory.Delete(tempDir, true);
        }

        [Fact]
        public void SetEnvValue_ShouldUpdateValue_WhenKeyExists()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            var envPath = Path.Combine(tempDir, ".env");
            File.WriteAllText(envPath, "DEBUG=true\n");

            var manager = new EnvironmentManager();

            // Act
            manager.SetEnvValue(tempDir, "DEBUG", "false");

            // Assert
            var lines = File.ReadAllLines(envPath);
            Assert.Contains("DEBUG=false", lines);

            // Cleanup
            Directory.Delete(tempDir, true);
        }
    }
}
