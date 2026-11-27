using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Data.Entities;

namespace CharacterConceptAI.Server.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<CharacterVariant> CharacterVariants => Set<CharacterVariant>();
    public DbSet<CharacterAttributeDefinition> CharacterAttributeDefinitions => Set<CharacterAttributeDefinition>();
    public DbSet<CharacterAttributeValue> CharacterAttributeValues => Set<CharacterAttributeValue>();
    public DbSet<VariantAttributeValue> VariantAttributeValues => Set<VariantAttributeValue>();
    public DbSet<CharacterRevision> CharacterRevisions => Set<CharacterRevision>();
    public DbSet<CharacterTemplate> CharacterTemplates => Set<CharacterTemplate>();
    public DbSet<TemplateAttributeDefinition> TemplateAttributeDefinitions => Set<TemplateAttributeDefinition>();
    public DbSet<CharacterRelationship> CharacterRelationships => Set<CharacterRelationship>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<SceneCharacter> SceneCharacters => Set<SceneCharacter>();
    public DbSet<CharacterConcept> CharacterConcepts => Set<CharacterConcept>();
    public DbSet<ConceptVersion> ConceptVersions => Set<ConceptVersion>();
    public DbSet<ConceptApproval> ConceptApprovals => Set<ConceptApproval>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // EF InMemory cannot generate SQL rowversion. Emulate tokens only in the
        // simulated test provider; real SQL remains the concurrency authority.
        if (Database.IsInMemory())
        {
            foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                var token = entry.Metadata.FindProperty("RowVersion");
                if (token is not null) entry.Property("RowVersion").CurrentValue = Guid.NewGuid().ToByteArray();
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ProjectMember composite PK
        builder.Entity<ProjectMember>()
            .HasKey(pm => new { pm.ProjectId, pm.UserId });

        builder.Entity<ProjectMember>()
            .HasOne(pm => pm.Project)
            .WithMany(p => p.Members)
            .HasForeignKey(pm => pm.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ProjectMember>()
            .HasOne(pm => pm.User)
            .WithMany()
            .HasForeignKey(pm => pm.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // CharacterAttributeValue composite PK
        builder.Entity<CharacterAttributeValue>()
            .HasKey(cav => new { cav.CharacterId, cav.DefinitionId });

        builder.Entity<CharacterAttributeValue>()
            .HasOne(cav => cav.Character)
            .WithMany(c => c.AttributeValues)
            .HasForeignKey(cav => cav.CharacterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CharacterAttributeValue>()
            .HasOne(cav => cav.Definition)
            .WithMany()
            .HasForeignKey(cav => cav.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Character
        builder.Entity<Character>()
            .HasOne(c => c.Project)
            .WithMany(p => p.Characters)
            .HasForeignKey(c => c.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Character>()
            .HasIndex(c => new { c.ProjectId, c.Name });

        // CharacterVariant
        builder.Entity<CharacterVariant>()
            .HasOne(cv => cv.Character)
            .WithMany(c => c.Variants)
            .HasForeignKey(cv => cv.CharacterId)
            .OnDelete(DeleteBehavior.Cascade);

        // GenerationJob
        builder.Entity<GenerationJob>()
            .HasOne(g => g.Project)
            .WithMany(p => p.GenerationJobs)
            .HasForeignKey(g => g.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<GenerationJob>()
            .HasOne(g => g.Character)
            .WithMany(c => c.GenerationJobs)
            .HasForeignKey(g => g.CharacterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<GenerationJob>()
            .HasOne(g => g.Variant)
            .WithMany(v => v.GenerationJobs)
            .HasForeignKey(g => g.VariantId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<GenerationJob>()
            .HasOne(g => g.RequestedByUser)
            .WithMany()
            .HasForeignKey(g => g.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<GenerationJob>()
            .HasIndex(g => new { g.Status, g.RequestedAt });

        builder.Entity<GenerationJob>()
            .HasIndex(g => new { g.RequestedByUserId, g.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");

        builder.Entity<GenerationJob>()
            .HasOne(gj => gj.ReferenceConceptVersion)
            .WithMany()
            .HasForeignKey(gj => gj.ReferenceConceptVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // ImageAsset
        builder.Entity<ImageAsset>()
            .HasOne(ia => ia.Project)
            .WithMany(p => p.ImageAssets)
            .HasForeignKey(ia => ia.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<ImageAsset>()
            .HasOne(ia => ia.GenerationJob)
            .WithMany(gj => gj.OutputAssets)
            .HasForeignKey(ia => ia.GenerationJobId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ImageAsset>()
            .HasIndex(ia => ia.StorageKey)
            .IsUnique();

        // Script
        builder.Entity<Script>()
            .HasOne(s => s.Project)
            .WithMany(p => p.Scripts)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Scene
        builder.Entity<Scene>()
            .HasOne(sc => sc.Script)
            .WithMany(s => s.Scenes)
            .HasForeignKey(sc => sc.ScriptId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Scene>()
            .HasIndex(sc => new { sc.ScriptId, sc.OrderIndex });

        // SceneCharacter composite PK
        builder.Entity<SceneCharacter>()
            .HasKey(sc => new { sc.SceneId, sc.CharacterId });

        builder.Entity<SceneCharacter>()
            .HasOne(sc => sc.Scene)
            .WithMany(s => s.SceneCharacters)
            .HasForeignKey(sc => sc.SceneId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SceneCharacter>()
            .HasOne(sc => sc.Character)
            .WithMany(c => c.SceneCharacters)
            .HasForeignKey(sc => sc.CharacterId)
            .OnDelete(DeleteBehavior.Restrict);

        // VariantAttributeValue composite PK
        builder.Entity<VariantAttributeValue>()
            .HasKey(vav => new { vav.VariantId, vav.DefinitionId });

        builder.Entity<VariantAttributeValue>()
            .HasOne(vav => vav.Variant)
            .WithMany(v => v.AttributeValues)
            .HasForeignKey(vav => vav.VariantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<VariantAttributeValue>()
            .HasOne(vav => vav.Definition)
            .WithMany(d => d.VariantValues)
            .HasForeignKey(vav => vav.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // CharacterRevision
        builder.Entity<CharacterRevision>()
            .HasOne(cr => cr.Character)
            .WithMany(c => c.Revisions)
            .HasForeignKey(cr => cr.CharacterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CharacterRevision>()
            .HasIndex(cr => new { cr.CharacterId, cr.RevisionNumber })
            .IsUnique();

        // CharacterTemplate
        builder.Entity<CharacterTemplate>()
            .HasOne(ct => ct.Project)
            .WithMany()
            .HasForeignKey(ct => ct.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // TemplateAttributeDefinition composite PK
        builder.Entity<TemplateAttributeDefinition>()
            .HasKey(tad => new { tad.TemplateId, tad.DefinitionId });

        builder.Entity<TemplateAttributeDefinition>()
            .HasOne(tad => tad.Template)
            .WithMany(t => t.TemplateAttributes)
            .HasForeignKey(tad => tad.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TemplateAttributeDefinition>()
            .HasOne(tad => tad.Definition)
            .WithMany(d => d.TemplateAttributes)
            .HasForeignKey(tad => tad.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        // CharacterRelationship
        builder.Entity<CharacterRelationship>()
            .HasOne(cr => cr.Project)
            .WithMany()
            .HasForeignKey(cr => cr.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterRelationship>()
            .HasOne(cr => cr.SourceCharacter)
            .WithMany(c => c.SourceRelationships)
            .HasForeignKey(cr => cr.SourceCharacterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterRelationship>()
            .HasOne(cr => cr.TargetCharacter)
            .WithMany(c => c.TargetRelationships)
            .HasForeignKey(cr => cr.TargetCharacterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterRelationship>()
            .HasIndex(cr => new { cr.ProjectId, cr.SourceCharacterId, cr.TargetCharacterId, cr.Type })
            .IsUnique();

        // CharacterConcept
        builder.Entity<CharacterConcept>()
            .HasOne(cc => cc.Project)
            .WithMany(p => p.CharacterConcepts)
            .HasForeignKey(cc => cc.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterConcept>()
            .HasOne(cc => cc.Character)
            .WithMany(c => c.Concepts)
            .HasForeignKey(cc => cc.CharacterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterConcept>()
            .HasOne(cc => cc.Variant)
            .WithMany()
            .HasForeignKey(cc => cc.VariantId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<CharacterConcept>()
            .HasOne(cc => cc.RepresentativeVersion)
            .WithMany()
            .HasForeignKey(cc => cc.RepresentativeVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<CharacterConcept>()
            .HasIndex(cc => new { cc.ProjectId, cc.CharacterId });

        // ConceptVersion
        builder.Entity<ConceptVersion>()
            .HasOne(cv => cv.Concept)
            .WithMany(c => c.Versions)
            .HasForeignKey(cv => cv.ConceptId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ConceptVersion>()
            .HasOne(cv => cv.ParentVersion)
            .WithMany()
            .HasForeignKey(cv => cv.ParentVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<ConceptVersion>()
            .HasOne(cv => cv.GenerationJob)
            .WithMany()
            .HasForeignKey(cv => cv.GenerationJobId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ConceptVersion>()
            .HasOne(cv => cv.ImageAsset)
            .WithMany()
            .HasForeignKey(cv => cv.ImageAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<ConceptVersion>()
            .HasIndex(cv => new { cv.ConceptId, cv.VersionNumber })
            .IsUnique();

        // ConceptApproval
        builder.Entity<ConceptApproval>()
            .HasOne(ca => ca.ConceptVersion)
            .WithMany(cv => cv.Approvals)
            .HasForeignKey(ca => ca.ConceptVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ConceptApproval>()
            .HasOne(ca => ca.ReviewerUser)
            .WithMany()
            .HasForeignKey(ca => ca.ReviewerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<ConceptApproval>()
            .HasIndex(ca => new { ca.ConceptVersionId, ca.ReviewedAt });
    }
}
