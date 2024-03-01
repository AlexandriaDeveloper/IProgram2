﻿using Core.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure;

public class ApplicationContext : IdentityDbContext<ApplicationUser>
{

    public DbSet<Employee> Employees { get; set; }
    public DbSet<Department> Departments { get; set; }

    public ApplicationContext(DbContextOptions<ApplicationContext> options) : base(options) { }


    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Employee>()
        .HasIndex(e => e.NationalId).IsUnique();
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


        builder.Entity<Form>(entity =>
        {
            entity.HasOne(x => x.User).WithMany().HasForeignKey(k => k.CreatedBy);
        });
        base.OnModelCreating(builder);
    }
}
