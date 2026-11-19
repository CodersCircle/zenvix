using Microsoft.EntityFrameworkCore;
using Zenvix.Core.Models;

namespace Zenvix.Infrastructure.Data
{
    public class ZenvixDbContext : DbContext
    {
        public DbSet<Website> Websites { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zenvix.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Website>().HasKey(w => w.Id);
        }
    }
}
