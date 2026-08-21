using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Domain.ValueObjects;
using FluentValidation;

namespace Cmsify.Core.Validation;

public sealed class CreateWorkspaceCommandValidator : AbstractValidator<CreateWorkspaceCommand>
{
    public CreateWorkspaceCommandValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage);
        RuleFor(command => command.Description).MaximumLength(1_000);
    }
}

public sealed class UpdateWorkspaceCommandValidator : AbstractValidator<UpdateWorkspaceCommand>
{
    public UpdateWorkspaceCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage);
        RuleFor(command => command.Description).MaximumLength(1_000);
    }
}

public sealed class CreateTemplateCommandValidator : AbstractValidator<CreateTemplateCommand>
{
    public CreateTemplateCommandValidator()
    {
        RuleFor(command => command.WorkspaceId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage);
        RuleFor(command => command.Description).MaximumLength(1_000);
        RuleFor(command => command.TitleFieldKey).Matches("^[a-zA-Z][a-zA-Z0-9_]*$").When(command => command.TitleFieldKey is not null);
    }
}

public sealed class UpdateTemplateCommandValidator : AbstractValidator<UpdateTemplateCommand>
{
    public UpdateTemplateCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage);
        RuleFor(command => command.Description).MaximumLength(1_000);
        RuleFor(command => command.TitleFieldKey).Matches("^[a-zA-Z][a-zA-Z0-9_]*$").When(command => command.TitleFieldKey is not null);
    }
}

public sealed class CreateTemplateVersionCommandValidator : AbstractValidator<CreateTemplateVersionCommand>
{
    public CreateTemplateVersionCommandValidator()
    {
        RuleFor(command => command.TemplateId).NotEmpty();
        RuleFor(command => command.Notes).MaximumLength(2_000);
    }
}

public sealed class TemplateFieldInputValidator : AbstractValidator<TemplateFieldInput>
{
    public TemplateFieldInputValidator()
    {
        RuleFor(field => field.Key).NotEmpty().Matches("^[a-zA-Z][a-zA-Z0-9_]*$").MaximumLength(100);
        RuleFor(field => field.Label).NotEmpty().MaximumLength(200);
        RuleFor(field => field.Order).GreaterThanOrEqualTo(0);
        RuleFor(field => field.MinOccurrences).GreaterThanOrEqualTo(0);
        RuleFor(field => field)
            .Must(field => !field.MaxOccurrences.HasValue || field.MaxOccurrences.Value >= field.MinOccurrences)
            .WithMessage("MaxOccurrences must be greater than or equal to MinOccurrences.");
        RuleFor(field => field)
            .Must(field => field.IsOpen || (field.PrimitiveType.HasValue ? 1 : 0) + (field.TemplateId.HasValue ? 1 : 0) + (field.ComponentId.HasValue ? 1 : 0) == 1)
            .WithMessage("Constrained fields must define exactly one of PrimitiveType, TemplateId, or ComponentId.");
        RuleFor(field => field)
            .Must(field => !field.IsOpen || (!field.PrimitiveType.HasValue && !field.TemplateId.HasValue && !field.ComponentId.HasValue))
            .WithMessage("Open fields cannot define PrimitiveType or TemplateId.");
    }
}

public sealed class SaveTemplateVersionStructureCommandValidator : AbstractValidator<SaveTemplateVersionStructureCommand>
{
    public SaveTemplateVersionStructureCommandValidator()
    {
        RuleFor(command => command.TemplateVersionId).NotEmpty();
        RuleForEach(command => command.Fields).SetValidator(new TemplateFieldInputValidator());
        RuleFor(command => command.Fields)
            .Must(fields => fields.Select(field => field.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == fields.Count)
            .WithMessage("Template field keys must be unique within a template version.");
    }
}

public sealed class CreateContentItemCommandValidator : AbstractValidator<CreateContentItemCommand>
{
    public CreateContentItemCommandValidator()
    {
        RuleFor(command => command.WorkspaceId).NotEmpty();
        RuleFor(command => command.TemplateVersionId).NotEmpty();
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage).When(command => command.Slug is not null);
        RuleForEach(command => command.FieldValues).ChildRules(value =>
        {
            value.RuleFor(fieldValue => fieldValue.FieldId).NotEmpty();
            value.RuleFor(fieldValue => fieldValue.Order).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class UpdateContentItemCommandValidator : AbstractValidator<UpdateContentItemCommand>
{
    public UpdateContentItemCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Slug).Must(SlugRules.IsValid).WithMessage(SlugRules.ValidationMessage).When(command => command.Slug is not null);
    }
}

public sealed class CreateMediaAssetCommandValidator : AbstractValidator<CreateMediaAssetCommand>
{
    public CreateMediaAssetCommandValidator()
    {
        RuleFor(command => command.WorkspaceId).NotEmpty();
        RuleFor(command => command.FileName).NotEmpty().MaximumLength(255);
        RuleFor(command => command.MimeType).NotEmpty().MaximumLength(255);
        RuleFor(command => command.SizeBytes).GreaterThanOrEqualTo(0);
        RuleFor(command => command.StorageKey).NotEmpty().MaximumLength(1_000);
        RuleFor(command => command.StorageProvider).NotEmpty().MaximumLength(100);
        RuleFor(command => command.AltText).MaximumLength(500);
    }
}

public sealed class UpdateMediaAssetCommandValidator : AbstractValidator<UpdateMediaAssetCommand>
{
    public UpdateMediaAssetCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.FileName).NotEmpty().MaximumLength(255);
        RuleFor(command => command.AltText).MaximumLength(500);
    }
}

public sealed class UpsertTagCommandValidator : AbstractValidator<UpsertTagCommand>
{
    public UpsertTagCommandValidator()
    {
        RuleFor(command => command.WorkspaceId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(command => command.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(command => command.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(command => command.TemporaryPassword).NotEmpty().MinimumLength(12);
        RuleFor(command => command.TimeZoneId).MaximumLength(100);
    }
}

public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(command => command.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(command => command.TimeZoneId).MaximumLength(100);
    }
}

public sealed class CreateApiClientCommandValidator : AbstractValidator<CreateApiClientCommand>
{
    public CreateApiClientCommandValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Description).MaximumLength(1_000);
        RuleFor(command => command.CreatedByUserId).NotEmpty();
        RuleFor(command => command.ExpiresAt)
            .Must(expiresAt => !expiresAt.HasValue || expiresAt.Value > DateTimeOffset.UtcNow)
            .WithMessage("ExpiresAt must be in the future.");
    }
}

public sealed class UpdateApiClientCommandValidator : AbstractValidator<UpdateApiClientCommand>
{
    public UpdateApiClientCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Description).MaximumLength(1_000);
    }
}

public sealed class CreateWebhookEndpointCommandValidator : AbstractValidator<CreateWebhookEndpointCommand>
{
    public CreateWebhookEndpointCommandValidator()
    {
        RuleFor(command => command.WorkspaceId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Url).NotEmpty().Must(BeAbsoluteHttpUrl).WithMessage("Webhook URL must be an absolute HTTP or HTTPS URL.");
        RuleFor(command => command.EventTypes).NotEmpty();
        RuleFor(command => command.CreatedByUserId).NotEmpty();
    }

    private static bool BeAbsoluteHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }
}

public sealed class UpdateWebhookEndpointCommandValidator : AbstractValidator<UpdateWebhookEndpointCommand>
{
    public UpdateWebhookEndpointCommandValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Url).NotEmpty().Must(BeAbsoluteHttpUrl).WithMessage("Webhook URL must be an absolute HTTP or HTTPS URL.");
        RuleFor(command => command.EventTypes).NotEmpty();
    }

    private static bool BeAbsoluteHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }
}
