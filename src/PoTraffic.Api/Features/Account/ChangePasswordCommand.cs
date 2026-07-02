using FluentValidation;
using PoTraffic.Api.Infrastructure.Storage;



namespace PoTraffic.Api.Features.Account;

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword, string ConfirmNewPassword) : IRequest<ChangePasswordResult>;
public sealed record ChangePasswordResult(bool IsSuccess, string? ErrorCode = null);

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty();
        RuleFor(c => c.NewPassword).NotEmpty().MinimumLength(8);
        RuleFor(c => c.ConfirmNewPassword).Equal(c => c.NewPassword).WithMessage("Passwords do not match.");
    }
}

public sealed class ChangePasswordHandler : IRequestHandler<ChangePasswordCommand, ChangePasswordResult>
{
    private readonly TableStorageContext _db;

    public ChangePasswordHandler(TableStorageContext db) => _db = db;

    public async Task<ChangePasswordResult> Handle(ChangePasswordCommand command, CancellationToken ct)
    {
        User? user = _db.Users
            .FirstOrDefault(u => u.Id == command.UserId);

        if (user is null) return new ChangePasswordResult(false, "USER_NOT_FOUND");

        if (!BCrypt.Net.BCrypt.Verify(command.CurrentPassword, user.PasswordHash))
            return new ChangePasswordResult(false, "INVALID_CURRENT_PASSWORD");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(command.NewPassword);
        await _db.SaveChangesAsync(ct);

        return new ChangePasswordResult(true);
    }
}
