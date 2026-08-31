using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cmsify.Api;

public static class CmsifySwaggerExtensions
{
    public static IServiceCollection AddCmsifySwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(Configure);
        return services;
    }

    private static void Configure(SwaggerGenOptions options)
    {
        options.SupportNonNullableReferenceTypes();
        options.NonNullableReferenceTypesAsRequired();
        options.OperationFilter<SwaggerAnonymousOperationFilter>();
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Cmsify API",
            Version = "v1",
            Description = "Headless CMS API"
        });
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter a Cmsify user session token, API client token, or JWT bearer token."
        });
        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer", document, null),
                []
            }
        });

        var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }
    }
}
