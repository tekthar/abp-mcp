using AbpMcp.Attributes;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Sample.Library;

/// <summary>Member registration and lifecycle (active/suspended).</summary>
[McpTool]
public sealed class MemberAppService : IMemberAppService, ITransientDependency
{
    private readonly LibraryDbContext _db;

    public MemberAppService(LibraryDbContext db) => _db = db;

    public async Task<MemberDto> RegisterAsync(RegisterMemberDto input)
    {
        var email = input.Email.Trim().ToLowerInvariant();
        var exists = await _db.Members.AnyAsync(m => m.Email == email).ConfigureAwait(false);
        if (exists)
        {
            throw new BusinessException("LIBRARY:MEMBER_EMAIL_TAKEN", $"A member with email '{email}' already exists.");
        }

        var entity = new Member
        {
            Id = Guid.NewGuid(),
            Name = input.Name.Trim(),
            Email = email,
            MemberSince = DateTime.UtcNow,
            Status = MemberStatus.Active,
        };
        _db.Members.Add(entity);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<MemberDto> GetAsync(Guid id)
    {
        var entity = await _db.Members.FindAsync(id).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:MEMBER_NOT_FOUND", $"No member with id {id}.");
        return Map(entity);
    }

    public async Task<IReadOnlyList<MemberDto>> ListAsync()
    {
        var rows = await _db.Members.AsNoTracking()
            .OrderBy(m => m.Name)
            .Take(500)
            .ToListAsync()
            .ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<MemberDto> SuspendAsync(SuspendMemberDto input)
    {
        var entity = await _db.Members.FindAsync(input.Id).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:MEMBER_NOT_FOUND", $"No member with id {input.Id}.");
        entity.Status = MemberStatus.Suspended;
        entity.SuspensionReason = input.Reason.Trim();
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<MemberDto> ReinstateAsync(Guid id)
    {
        var entity = await _db.Members.FindAsync(id).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:MEMBER_NOT_FOUND", $"No member with id {id}.");
        entity.Status = MemberStatus.Active;
        entity.SuspensionReason = null;
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return Map(entity);
    }

    private static MemberDto Map(Member m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Email = m.Email,
        MemberSince = m.MemberSince,
        Status = m.Status,
        SuspensionReason = m.SuspensionReason,
    };
}

public interface IMemberAppService : IApplicationService
{
    Task<MemberDto> RegisterAsync(RegisterMemberDto input);
    Task<MemberDto> GetAsync(Guid id);
    Task<IReadOnlyList<MemberDto>> ListAsync();
    Task<MemberDto> SuspendAsync(SuspendMemberDto input);
    Task<MemberDto> ReinstateAsync(Guid id);
}
