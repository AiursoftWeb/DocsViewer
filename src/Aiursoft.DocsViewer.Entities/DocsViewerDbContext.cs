using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DocsViewer.Entities;

[ExcludeFromCodeCoverage]

public abstract class DocsViewerDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<LocalizedDocument> LocalizedDocuments => Set<LocalizedDocument>();
    public DbSet<LocalizedNavTitle> LocalizedNavTitles => Set<LocalizedNavTitle>();
    public DbSet<DocumentComment> DocumentComments => Set<DocumentComment>();
    public DbSet<DocumentLike> DocumentLikes => Set<DocumentLike>();
    public DbSet<DocumentFavorite> DocumentFavorites => Set<DocumentFavorite>();
    public DbSet<SearchEmbedding> SearchEmbeddings => Set<SearchEmbedding>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        
        builder.Entity<DocumentFavorite>().HasKey(f => new { f.UserId, f.DocumentId });
        builder.Entity<DocumentFavorite>()
            .HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<DocumentLike>().HasKey(l => new { l.UserId, l.DocumentId });
        builder.Entity<DocumentLike>()
            .HasOne(l => l.User)
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<DocumentComment>()
            .HasOne(c => c.ParentComment)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentCommentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<DocumentComment>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
            
        builder.Entity<LocalizedDocument>()
            .HasIndex(ld => new { ld.DocumentId, ld.Culture })
            .IsUnique();

        builder.Entity<LocalizedNavTitle>()
            .HasIndex(nt => new { nt.SourceText, nt.Culture })
            .IsUnique();

        builder.Entity<Document>().HasQueryFilter(d => !d.IsDeleted);
        builder.Entity<DocumentComment>().HasQueryFilter(c => !c.Document.IsDeleted);
        builder.Entity<DocumentLike>().HasQueryFilter(l => !l.Document.IsDeleted);
        builder.Entity<DocumentFavorite>().HasQueryFilter(f => !f.Document.IsDeleted);
        builder.Entity<LocalizedDocument>().HasQueryFilter(ld => !ld.Document.IsDeleted);
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}
