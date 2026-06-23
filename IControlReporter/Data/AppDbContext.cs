using Microsoft.EntityFrameworkCore;
using IControlReporter.Models;

namespace IControlReporter.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<analogevents> AnalogEvents { get; set; }
        public DbSet<points> Points { get; set; }
    
    }
}