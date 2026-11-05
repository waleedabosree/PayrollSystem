using Microsoft.EntityFrameworkCore;
using Payroll.Shared.Domain;

namespace Payroll.Shared.Data;

/// <summary>
/// System of record. Everything the dashboard shows, plus the outbox, lives here.
///
/// Migrations are the deployment mechanism (`dotnet ef migrations add ...`), never EnsureCreated,
/// because EnsureCreated and migrations are mutually exclusive states that silently diverge
/// between environments.
/// </summary>
public class PayrollDbContext : DbContext
{
    public PayrollDbContext(DbContextOptions<PayrollDbContext> options) : base(options) { }

    public DbSet<PayrollBatch> PayrollBatches => Set<PayrollBatch>();
    public DbSet<PayrollResult> PayrollResults => Set<PayrollResult>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PayrollBatch>(entity =>
        {
            entity.ToTable("PayrollBatches");
            entity.HasKey(b => b.Id);

            entity.Property(b => b.EntityId).HasMaxLength(64).IsRequired();
            entity.Property(b => b.OriginalFileName).HasMaxLength(260).IsRequired();
            entity.Property(b => b.BlobContainer).HasMaxLength(64);
            entity.Property(b => b.BlobName).HasMaxLength(512);
            entity.Property(b => b.BlobETag).HasMaxLength(128);
            entity.Property(b => b.CorrelationId).HasMaxLength(64).IsRequired();
            entity.Property(b => b.FailureReason).HasMaxLength(2000);
            entity.Property(b => b.FailureStage).HasMaxLength(64);

            // Enums are stored as STRINGS on purpose.
            // Storing an int means inserting an enum value mid-sequence later forces a resync of
            // every existing row; as a string, new states simply never appear on old rows.
            entity.Property(b => b.Status)
                  .HasConversion<string>()
                  .HasMaxLength(32)
                  .IsRequired();

            entity.HasIndex(b => b.Status);
            entity.HasIndex(b => b.CreatedAtUtc);
            entity.HasIndex(b => b.CorrelationId);

            entity.HasMany(b => b.Results)
                  .WithOne(r => r.Batch!)
                  .HasForeignKey(r => r.BatchId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PayrollResult>(entity =>
        {
            entity.ToTable("PayrollResults");
            entity.HasKey(r => r.Id);

            entity.Property(r => r.EmployeeId).HasMaxLength(20).IsRequired();
            entity.Property(r => r.EmployeeName).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Currency).HasMaxLength(3).IsRequired();

            entity.Property(r => r.RegularHours).HasPrecision(18, 4);
            entity.Property(r => r.OvertimeHours).HasPrecision(18, 4);
            entity.Property(r => r.HourlyRate).HasPrecision(18, 4);
            entity.Property(r => r.Deductions).HasPrecision(18, 4);
            entity.Property(r => r.GrossPay).HasPrecision(18, 4);
            entity.Property(r => r.NetPay).HasPrecision(18, 4);

            // THE idempotency guard. A duplicate message delivery attempting a second insert for
            // the same employee in the same batch throws here instead of silently double-paying --
            // which is a far more visible and far more recoverable failure.
            entity.HasIndex(r => new { r.BatchId, r.EmployeeId }).IsUnique();

            // Supports "show me row 12" style error reporting when a business rule rejects a line.
            entity.HasIndex(r => new { r.BatchId, r.SourceRowNumber });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(m => m.Id);

            entity.Property(m => m.Payload).IsRequired();
            entity.Property(m => m.CorrelationId).HasMaxLength(64).IsRequired();
            entity.Property(m => m.LastError).HasMaxLength(2000);

            entity.Property(m => m.State)
                  .HasConversion<string>()
                  .HasMaxLength(32)
                  .IsRequired();

            // Composite so the dispatcher's "pending, oldest first" scan uses one seek
            // instead of sorting every outbox row on each tick.
            entity.HasIndex(m => new { m.State, m.CreatedAtUtc });
        });
    }
}