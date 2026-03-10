using Core.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure;

public class ApplicationContext : IdentityDbContext<ApplicationUser>
{

    public DbSet<Employee> Employees { get; set; }
    public DbSet<Department> Departments { get; set; }
    public DbSet<EmployeeNetPay> EmployeeNetPays { get; set; }

    public ApplicationContext(DbContextOptions<ApplicationContext> options) : base(options) { }
    protected ApplicationContext(DbContextOptions options) : base(options) { }


    protected override void OnModelCreating(ModelBuilder builder)
    {
        // builder.Entity<Employee>()
        // .HasIndex(e => e.NationalId).IsUnique();
        builder.Entity<Employee>()
        .HasIndex(e => e.TegaraCode).IsUnique();
        builder.Entity<Employee>()
        .HasIndex(e => e.TabCode).IsUnique();


        builder.Entity<EmployeeBank>(entity =>
        {
            entity.HasKey(e => e.EmployeeId);
            entity.HasOne(e => e.Employee)
            .WithOne(e => e.EmployeeBank)
            .HasForeignKey<EmployeeBank>(e => e.EmployeeId);


        });

        builder.Entity<Daily>(entity =>
        {
            entity.HasMany(x => x.Forms).WithOne(x => x.Daily).HasForeignKey(k => k.DailyId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Department>(entity =>
        {
            entity.HasMany(x => x.Employees).WithOne(x => x.Department).HasForeignKey(k => k.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Form>(entity =>
        {
            entity.HasOne(x => x.User).WithMany().HasForeignKey(k => k.CreatedBy);
        });

        builder.Entity<FormDetails>(entity =>
        {
            entity.HasIndex(fd => fd.FormId);
        });

        builder.Entity<EmployeeNetPay>(entity =>
        {
            entity.HasIndex(e => new { e.DailyId, e.EmployeeId }).IsUnique();
        });

        base.OnModelCreating(builder);
    }
}
