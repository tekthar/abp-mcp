using AbpMcp.Attributes;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.DependencyInjection;

namespace AbpMcp.Sample.Library;

/// <summary>
/// Loan lifecycle: check out, return, renew, list. The "agent does something useful"
/// surface — this is what makes a video demo feel real.
/// </summary>
[McpTool]
public sealed class LoanAppService : ILoanAppService, ITransientDependency
{
    private readonly LibraryDbContext _db;

    public LoanAppService(LibraryDbContext db) => _db = db;

    public async Task<LoanDto> CheckOutAsync(CheckOutDto input)
    {
        var member = await _db.Members.FindAsync(input.MemberId).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:MEMBER_NOT_FOUND", $"No member with id {input.MemberId}.");
        if (member.Status == MemberStatus.Suspended)
        {
            throw new BusinessException("LIBRARY:MEMBER_SUSPENDED",
                $"Member '{member.Name}' is suspended ({member.SuspensionReason}). Reinstate before checking out.");
        }

        var edition = await _db.Editions
            .Include(e => e.Title)
            .Include(e => e.Loans)
            .FirstOrDefaultAsync(e => e.Id == input.EditionId).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:EDITION_NOT_FOUND", $"No edition with id {input.EditionId}.");

        var activeLoans = edition.Loans.Count(l => l.Status == LoanStatus.Active);
        if (activeLoans >= edition.TotalCopies)
        {
            throw new BusinessException("LIBRARY:NO_COPIES_AVAILABLE",
                $"All {edition.TotalCopies} copies of '{edition.Title?.Name}' ({edition.Format}) are currently checked out.");
        }

        var days = Math.Clamp(input.DurationDays, 1, 90);
        var now = DateTime.UtcNow;
        var loan = new Loan
        {
            Id = Guid.NewGuid(),
            EditionId = edition.Id,
            MemberId = member.Id,
            CheckedOutAt = now,
            DueAt = now.AddDays(days),
            Status = LoanStatus.Active,
        };
        _db.Loans.Add(loan);
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return Map(loan, edition, member);
    }

    public async Task<LoanDto> ReturnAsync(Guid loanId)
    {
        var loan = await LoadLoanAsync(loanId).ConfigureAwait(false);
        if (loan.Status == LoanStatus.Returned)
        {
            throw new BusinessException("LIBRARY:LOAN_ALREADY_RETURNED",
                $"Loan {loanId} was already returned at {loan.ReturnedAt:u}.");
        }

        loan.ReturnedAt = DateTime.UtcNow;
        loan.Status = LoanStatus.Returned;
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return Map(loan, loan.Edition!, loan.Member!);
    }

    public async Task<LoanDto> RenewAsync(RenewLoanDto input)
    {
        var loan = await LoadLoanAsync(input.LoanId).ConfigureAwait(false);
        if (loan.Status != LoanStatus.Active)
        {
            throw new BusinessException("LIBRARY:LOAN_NOT_ACTIVE",
                $"Cannot renew loan {input.LoanId} — its status is {loan.Status}.");
        }

        var extra = Math.Clamp(input.AdditionalDays, 1, 30);
        loan.DueAt = loan.DueAt.AddDays(extra);
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return Map(loan, loan.Edition!, loan.Member!);
    }

    public async Task<IReadOnlyList<LoanDto>> ListForMemberAsync(Guid memberId)
    {
        var rows = await _db.Loans
            .Include(l => l.Edition).ThenInclude(e => e!.Title)
            .Include(l => l.Member)
            .Where(l => l.MemberId == memberId)
            .OrderByDescending(l => l.CheckedOutAt)
            .Take(100)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(l => Map(l, l.Edition!, l.Member!)).ToList();
    }

    public async Task<IReadOnlyList<LoanDto>> ListOverdueAsync()
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Loans
            .Include(l => l.Edition).ThenInclude(e => e!.Title)
            .Include(l => l.Member)
            .Where(l => l.Status == LoanStatus.Active && l.DueAt < now)
            .OrderBy(l => l.DueAt)
            .Take(100)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(l => Map(l, l.Edition!, l.Member!)).ToList();
    }

    private async Task<Loan> LoadLoanAsync(Guid loanId)
    {
        return await _db.Loans
            .Include(l => l.Edition).ThenInclude(e => e!.Title)
            .Include(l => l.Member)
            .FirstOrDefaultAsync(l => l.Id == loanId).ConfigureAwait(false)
            ?? throw new BusinessException("LIBRARY:LOAN_NOT_FOUND", $"No loan with id {loanId}.");
    }

    private static LoanDto Map(Loan l, Edition edition, Member member)
    {
        var isOverdue = l.Status == LoanStatus.Active && l.DueAt < DateTime.UtcNow;
        return new LoanDto
        {
            Id = l.Id,
            EditionId = l.EditionId,
            MemberId = l.MemberId,
            TitleName = edition.Title?.Name ?? string.Empty,
            Format = edition.Format,
            MemberName = member.Name,
            CheckedOutAt = l.CheckedOutAt,
            DueAt = l.DueAt,
            ReturnedAt = l.ReturnedAt,
            Status = isOverdue ? LoanStatus.Overdue : l.Status,
            IsOverdue = isOverdue,
        };
    }
}

public interface ILoanAppService : IApplicationService
{
    Task<LoanDto> CheckOutAsync(CheckOutDto input);
    Task<LoanDto> ReturnAsync(Guid loanId);
    Task<LoanDto> RenewAsync(RenewLoanDto input);
    Task<IReadOnlyList<LoanDto>> ListForMemberAsync(Guid memberId);
    Task<IReadOnlyList<LoanDto>> ListOverdueAsync();
}
