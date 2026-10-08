using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IEnsureEmbeddedTemplateRequestHandler
{
    Task<Result<EmbeddedTemplateOutput>> HandleAsync(EnsureEmbeddedTemplateRequest request, CancellationToken cancellationToken);
}

public sealed class EnsureEmbeddedTemplateRequestHandler(IEmbeddedTemplateRepository repository, ICurrentActor actor,
    IEmbeddedTemplateSetupAuthorizationService authorization) : IEnsureEmbeddedTemplateRequestHandler
{
    public async Task<Result<EmbeddedTemplateOutput>> HandleAsync(EnsureEmbeddedTemplateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated || actor.UserId is not { } subject || subject == Guid.Empty)
            return Failure("authentication-required", ResultErrorKind.Unauthenticated);
        if (request.WorkspaceId == Guid.Empty) return Failure(EmbeddedContentErrors.Validation, ResultErrorKind.Validation);
        var definition = EmbeddedContractRules.ValidateAndCopy(request.Contract);
        if (definition.IsFailure) return Result<EmbeddedTemplateOutput>.Failure(definition.Errors[0]);
        var scope = new EmbeddedTemplateContractScope(request.WorkspaceId, definition.Value.ContractKey,
            EmbeddedContractRules.Fingerprint(definition.Value));
        if (!await authorization.CanEnsureAsync(scope, cancellationToken)) return Failure("forbidden", ResultErrorKind.Forbidden);
        cancellationToken.ThrowIfCancellationRequested();
        return await repository.EnsureAsync(request with { Contract = definition.Value }, subject,
            authorization.CanEnsureAsync, cancellationToken);
    }

    private static Result<EmbeddedTemplateOutput> Failure(string code, ResultErrorKind kind)
        => Result<EmbeddedTemplateOutput>.Failure(new(code, "Permission denied or invalid request.", kind));
}
