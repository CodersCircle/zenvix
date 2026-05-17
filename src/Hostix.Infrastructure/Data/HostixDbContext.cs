using Microsoft.EntityFrameworkCore;
using Hostix.Core.Models;

namespace Hostix.Infrastructure.Data
{
    public class HostixDbContext : DbContext
    {
        public DbSet<Website> Websites { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hostix.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Website>().HasKey(w => w.Id);
        }
    }
}
