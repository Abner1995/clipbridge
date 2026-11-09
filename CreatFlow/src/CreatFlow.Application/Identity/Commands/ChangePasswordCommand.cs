namespace CreatFlow.Application.Identity.Commands;

public record ChangePasswordCommand(Guid MemberId, string OldPassword, string NewPassword);
