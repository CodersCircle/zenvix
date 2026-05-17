using System;
using System.IO;
using Serilog;

namespace Hostix.Modules.Services
{
    public interface ITemplateGenerator
    {
        void CreateFromTemplate(string templateName, string targetPath);
    }

    public class TemplateGenerator : ITemplateGenerator
    {
        public void CreateFromTemplate(string templateName, string targetPath)
        {
            Log.Information("Generating project from template: {Template} at {Path}", templateName, targetPath);
            
            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);

            // Simple template logic: Create a basic structure
            switch (templateName.ToLower())
            {
                case "laravel":
                    File.WriteAllText(Path.Combine(targetPath, "artisan"), "# Laravel Artisan Placeholder");
                    Directory.CreateDirectory(Path.Combine(targetPath, "public"));
                    File.WriteAllText(Path.Combine(targetPath, "public", "index.php"), "<?php echo 'Hello from Laravel!';");
                    break;

                case "vite":
                    File.WriteAllText(Path.Combine(targetPath, "vite.config.js"), "// Vite Config Placeholder");
                    Directory.CreateDirectory(Path.Combine(targetPath, "dist"));
                    File.WriteAllText(Path.Combine(targetPath, "dist", "index.html"), "<h1>Hello from Vite!</h1>");
                    break;

                default:
                    File.WriteAllText(Path.Combine(targetPath, "index.html"), "<h1>Generic Project</h1>");
                    break;
            }

            Log.Information("Template {Template} applied successfully.", templateName);
        }
    }
}
