using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Data;

/// <summary>
/// The store — one Postgres carrying both the time-series and the vector shapes (ADR-0004).
/// </summary>
/// <remarks>
/// The schema is catalogued in <c>documentation/data-dictionary.md</c>, and the two are kept in lockstep in
/// the same pull request. A data dictionary that lags the schema is worse than none, because it is read as
/// authoritative.
/// </remarks>
/// <param name="options">The context options.</param>
public sealed class TopstepXDbContext(DbContextOptions<TopstepXDbContext> options) : DbContext(options)
{
    /// <summary>The precision every stored price uses. Never a floating type.</summary>
    /// <remarks>
    /// A tick size of 0.25 or 0.01 has no exact binary representation, and an indicator accumulating over
    /// thousands of bars drifts. 8 decimal places is far more than any listed future needs, and costs nothing.
    /// </remarks>
    public const string PriceColumnType = "numeric(18,8)";

    /// <summary>
    /// The decimal scale of <see cref="PriceColumnType"/> — the number of places Postgres keeps.
    /// </summary>
    /// <remarks>
    /// <b>Must agree with <see cref="PriceColumnType"/>.</b> They cannot be derived from one another at
    /// compile time (const string concatenation cannot format an int), so a test asserts they match.
    /// <para>
    /// This exists because a value computed at full <see cref="decimal"/> precision and the same value read
    /// back from the database are <b>not equal</b>: the column rounds to this scale and the computation does
    /// not. Anything comparing a fresh computation against a stored row must round to this first, or the
    /// comparison is always false and every "has it changed?" check silently answers yes.
    /// </para>
    /// </remarks>
    public const int PriceScale = 8;

    /// <summary>The dimensionality every stored embedding uses.</summary>
    /// <remarks>
    /// A schema constant rather than configuration: the column type is <c>vector(N)</c>, so changing it is a
    /// migration. A provider emitting a different width needs that migration, not a setting.
    /// </remarks>
    public const int EmbeddingDimensions = 1024;

    /// <summary>The clean-historical bar store — the system of record.</summary>
    public DbSet<BarRecord> Bars => Set<BarRecord>();

    /// <summary>Pre-computed indicator values, a projection over <see cref="Bars"/>.</summary>
    public DbSet<IndicatorValueRecord> IndicatorValues => Set<IndicatorValueRecord>();

    /// <summary>Ranges the venue answered empty — the negative-result ledger.</summary>
    public DbSet<BarCoverageRecord> BarCoverage => Set<BarCoverageRecord>();

    /// <summary>Agent-recorded observations — original data, as is the tape.</summary>
    public DbSet<ObservationRecord> Observations => Set<ObservationRecord>();

    /// <summary>Vector embeddings over <see cref="Observations"/>.</summary>
    public DbSet<EmbeddingRecord> Embeddings => Set<EmbeddingRecord>();

    /// <summary>The trade tape — the order-flow system of record.</summary>
    public DbSet<TradeRecord> Trades => Set<TradeRecord>();

    /// <summary>Ranges during which a subscription was listening — the tape's coverage ledger.</summary>
    public DbSet<TapeCoverageRecord> TapeCoverage => Set<TapeCoverageRecord>();

    /// <summary>Buy and sell volume per price per bar — a projection over <see cref="Trades"/>.</summary>
    public DbSet<FootprintCellRecord> FootprintCells => Set<FootprintCellRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BarRecord>(entity =>
        {
            entity.ToTable("Bars");
            entity.HasKey(b => new { b.Venue, b.Instrument, b.ResolutionMinutes, b.BucketStart });

            entity.Property(b => b.Venue).HasMaxLength(64);
            entity.Property(b => b.Instrument).HasMaxLength(32);

            // Nullable on purpose, and never backfilled: rows written before this column existed have no
            // contract, and the information was not captured. An inferred front month would be a guess, which
            // is the failure this column exists to stop (ADR-0011).
            entity.Property(b => b.ContractId).HasMaxLength(64);

            entity.Property(b => b.Open).HasColumnType(PriceColumnType);
            entity.Property(b => b.High).HasColumnType(PriceColumnType);
            entity.Property(b => b.Low).HasColumnType(PriceColumnType);
            entity.Property(b => b.Close).HasColumnType(PriceColumnType);

            // The shape of every read: one instrument, one resolution, a window.
            entity.HasIndex(b => new { b.Instrument, b.ResolutionMinutes, b.BucketStart });
        });

        modelBuilder.Entity<IndicatorValueRecord>(entity =>
        {
            entity.ToTable("IndicatorValues");
            entity.HasKey(v => new
            {
                v.Venue,
                v.Instrument,
                v.ResolutionMinutes,
                v.Indicator,
                v.Period,
                v.BucketStart,
            });

            entity.Property(v => v.Venue).HasMaxLength(64);
            entity.Property(v => v.Instrument).HasMaxLength(32);
            entity.Property(v => v.Indicator).HasMaxLength(32);
            entity.Property(v => v.Value).HasColumnType(PriceColumnType);

            entity.HasIndex(v => new
            {
                v.Instrument,
                v.ResolutionMinutes,
                v.Indicator,
                v.Period,
                v.BucketStart,
            });
        });

        modelBuilder.Entity<BarCoverageRecord>(entity =>
        {
            entity.ToTable("BarCoverage");
            entity.HasKey(c => new
            {
                c.Venue,
                c.Instrument,
                c.ResolutionMinutes,
                c.RangeStart,
                c.RangeEnd,
            });

            entity.Property(c => c.Venue).HasMaxLength(64);
            entity.Property(c => c.Instrument).HasMaxLength(32);

            entity.HasIndex(c => new { c.Instrument, c.ResolutionMinutes, c.RangeStart, c.RangeEnd });
        });

        modelBuilder.Entity<TradeRecord>(entity =>
        {
            entity.ToTable("Trades");
            entity.HasKey(t => new { t.Venue, t.Instrument, t.ContractId, t.TradeTimeUtc, t.Sequence });

            entity.Property(t => t.Venue).HasMaxLength(64);
            entity.Property(t => t.Instrument).HasMaxLength(32);
            entity.Property(t => t.ContractId).HasMaxLength(64);
            entity.Property(t => t.Price).HasColumnType(PriceColumnType);
            entity.Property(t => t.Direction).HasConversion<int>();

            // The shape of every read: one instrument, one contract, a window.
            entity.HasIndex(t => new { t.Instrument, t.ContractId, t.TradeTimeUtc });
        });

        modelBuilder.Entity<TapeCoverageRecord>(entity =>
        {
            entity.ToTable("TapeCoverage");
            entity.HasKey(c => new { c.Venue, c.Instrument, c.ContractId, c.RangeStart, c.RangeEnd });

            entity.Property(c => c.Venue).HasMaxLength(64);
            entity.Property(c => c.Instrument).HasMaxLength(32);
            entity.Property(c => c.ContractId).HasMaxLength(64);

            entity.HasIndex(c => new { c.Instrument, c.ContractId, c.RangeStart, c.RangeEnd });
        });

        modelBuilder.Entity<FootprintCellRecord>(entity =>
        {
            entity.ToTable("FootprintCells");
            entity.HasKey(c => new { c.Venue, c.Instrument, c.ResolutionMinutes, c.BucketStart, c.Price });

            entity.Property(c => c.Venue).HasMaxLength(64);
            entity.Property(c => c.Instrument).HasMaxLength(32);
            entity.Property(c => c.Price).HasColumnType(PriceColumnType);

            entity.HasIndex(c => new { c.Instrument, c.ResolutionMinutes, c.BucketStart });
        });

        modelBuilder.Entity<ObservationRecord>(entity =>
        {
            entity.ToTable("Observations");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Instrument).HasMaxLength(32);
            entity.Property(o => o.Kind).HasMaxLength(32);

            entity.HasIndex(o => new { o.Instrument, o.RecordedAt });
        });

        // pgvector only maps under Npgsql. Configuring this unconditionally breaks every provider-agnostic
        // test, so the entity is left out of the model entirely rather than half-configured.
        if (Database.IsNpgsql())
        {
            // REQUIRED, not probed. A vector(N) column cannot exist without the extension, so unlike the
            // Timescale hypertable -- which is a performance property and degrades to a plain table -- there
            // is nothing to degrade to here. The compose image and the test image both carry it.
            modelBuilder.HasPostgresExtension("vector");

            modelBuilder.Entity<EmbeddingRecord>(entity =>
            {
                entity.ToTable("Embeddings", table =>
                    table.HasCheckConstraint("CK_Embeddings_OwnerKindKnown", "\"OwnerKind\" <> 0"));

                entity.HasKey(e => new { e.OwnerKind, e.OwnerId, e.Model });
                entity.Property(e => e.OwnerKind).HasConversion<int>();
                entity.Property(e => e.OwnerId).HasMaxLength(512);
                entity.Property(e => e.Model).HasMaxLength(128);
                entity.Property(e => e.ContentHash).HasMaxLength(64);
                entity.Property(e => e.Embedding).HasColumnType("vector(" + EmbeddingDimensions + ")");
            });
        }
        else
        {
            modelBuilder.Ignore<EmbeddingRecord>();
        }
    }
}
