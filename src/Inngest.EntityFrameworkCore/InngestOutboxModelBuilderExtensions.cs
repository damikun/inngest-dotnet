// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Microsoft.EntityFrameworkCore;

public static class InngestOutboxModelBuilderExtensions
{
    // Entity Framework returns the times of some providers with an unspecified kind. Here they are always UTC.
    private static readonly ValueConverter<DateTime, DateTime> Utc = new(
        time => time,
        time => DateTime.SpecifyKind(time, DateTimeKind.Utc));

    /// <summary>
    /// Maps the outbox table, InngestOutbox by default, in the model of a DbContext. The table is part of the app's
    /// schema like any other, so add a migration after calling this. configure changes the mapping, for example to move
    /// the table to another schema with ToTable("Outbox", "inngest").
    /// </summary>
    public static ModelBuilder AddInngestOutbox(this ModelBuilder modelBuilder, Action<EntityTypeBuilder<InngestOutboxMessage>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entity = modelBuilder.Entity<InngestOutboxMessage>();
        entity.ToTable("InngestOutbox");
        entity.HasKey(message => message.Id);

        // The id is chosen when the event is added, by the code or by AddInngestEvent, and never by the database.
        entity.Property(message => message.Id).HasMaxLength(OutboxEventWriter.MaxTextLength).ValueGeneratedNever();
        entity.Property(message => message.Name).HasMaxLength(OutboxEventWriter.MaxTextLength).IsRequired();
        entity.Property(message => message.Data).IsRequired();
        entity.Property(message => message.Version).HasMaxLength(OutboxEventWriter.MaxTextLength);
        entity.Property(message => message.Timestamp).HasConversion(Utc);
        entity.Property(message => message.NextAttemptAt).HasConversion(Utc);
        entity.Property(message => message.LastError).HasMaxLength(2000);
        entity.Property(message => message.LeaseToken).HasMaxLength(64);
        entity.Property(message => message.LeaseExpiresAt).HasConversion(Utc);
        entity.Property(message => message.SentAt).HasConversion(Utc);

        // What the dispatcher asks for: the events not sent yet that are due, oldest first, and the sent ones that are old enough to delete.
        entity.HasIndex(message => new { message.SentAt, message.NextAttemptAt });

        configure?.Invoke(entity);
        return modelBuilder;
    }
}
